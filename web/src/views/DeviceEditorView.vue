<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  ApiRequestError, createDevice, getDeviceGroups, getDeviceInventoryDetail, testDeviceConnectivity, updateDevice,
  type DeviceConnectivityTestResult, type DeviceGroup,
} from '../api/client'
import NcLayout from '../components/NcLayout.vue'
import {
  DEVICE_CATALOG, categoriesForVendor, subtypesFor, subtypeByBackendType,
  type DeviceSubtypeDefinition,
} from '../deviceCatalog'
import { useAuthStore } from '../stores/auth'
import { useSiteContextStore } from '../stores/siteContext'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const siteContext = useSiteContextStore()
const editingId = computed(() => String(route.params.id || ''))
const isEdit = computed(() => editingId.value.length > 0)
const canWrite = computed(() => auth.user?.role === 'SUPERADMIN')
const loading = ref(false)
const loadingDetail = ref(false)
const groups = ref<DeviceGroup[]>([])
const advancedOpen = ref<string[]>([])
const connectivity = ref<DeviceConnectivityTestResult | null>(null)
const connectivityLoading = ref(false)
const connectivityController = ref<AbortController | null>(null)
const idempotencyKey = ref(newIdempotencyKey())
const formErrors = reactive<Record<string, string>>({})
const form = reactive({
  id: '', name: '', siteId: '', groupId: '', vendor: 'H3C', category: 'NETWORK_DEVICE', subtype: '', model: '',
  managementAddress: '', protocol: 'SSH' as 'SSH' | 'TELNET', port: 22, username: '', credentialReference: '',
  hostKeySha256: '', terminalEncoding: 'AUTO' as 'AUTO' | 'UTF-8' | 'GB18030', timeoutMs: 10000,
  description: '', enabled: true,
})

const vendors = DEVICE_CATALOG
const legacySubtype = ref<DeviceSubtypeDefinition | null>(null)
const vendorOptions = computed(() => isEdit.value && form.vendor && !vendors.some(vendor => vendor.key === form.vendor)
  ? [...vendors, { key: form.vendor, label: form.vendor, categories: [] }]
  : vendors)
const categories = computed(() => {
  const current = categoriesForVendor(form.vendor)
  return current.length || !legacySubtype.value ? current : [{ key: 'LEGACY', label: '现有类别', subtypes: [legacySubtype.value] }]
})
const subtypes = computed(() => {
  const current = subtypesFor(form.vendor, form.category)
  return legacySubtype.value && !current.some(item => item.backendType === legacySubtype.value?.backendType)
    ? [...current, legacySubtype.value]
    : current
})
const selectedSubtype = computed<DeviceSubtypeDefinition | undefined>(() => subtypes.value.find(item => item.key === form.subtype))
const currentSite = computed(() => siteContext.activeSites.find(site => site.id === form.siteId) || null)
const connectionReadOnly = computed(() => isEdit.value)
const pageTitle = computed(() => isEdit.value ? '编辑设备' : '新增设备')
const submitLabel = computed(() => isEdit.value ? '保存设备信息' : '创建设备')

function clearErrors(): void { Object.keys(formErrors).forEach(key => delete formErrors[key]) }
function setError(field: string, message: string): void { formErrors[field] = message }
function newIdempotencyKey(): string { return globalThis.crypto?.randomUUID?.() || `device-${Date.now()}-${Math.random().toString(16).slice(2)}` }

async function refreshGroups(): Promise<void> {
  if (!form.siteId) { groups.value = []; return }
  try { groups.value = await getDeviceGroups(form.siteId) }
  catch { groups.value = []; ElMessage.error('设备分组读取失败，保存前无法确认分组归属') }
}

function chooseVendor(value: string): void {
  form.vendor = value
  form.category = categoriesForVendor(value)[0]?.key || ''
  form.subtype = ''
}
function chooseCategory(value: string): void { form.category = value; form.subtype = '' }
function chooseSubtype(value: string): void {
  form.subtype = value
  const item = subtypes.value.find(subtype => subtype.key === value)
  if (item?.creation === 'RESTRICTED') setError('subtype', item.note || '该设备类型暂不支持新增')
  else delete formErrors.subtype
}
function changeProtocol(value: 'SSH' | 'TELNET'): void { form.protocol = value; if (!isEdit.value) form.port = value === 'SSH' ? 22 : 23 }

function validate(): boolean {
  clearErrors()
  const site = currentSite.value
  if (!canWrite.value) setError('form', '当前身份没有设备写入权限。')
  if (!site || site.status !== 'ACTIVE') setError('siteId', '请选择当前用户有权限且处于启用状态的局点。')
  if (!form.name.trim()) setError('name', '设备名称为必填项。')
  if (!form.managementAddress.trim()) setError('managementAddress', '管理地址为必填项。')
  if (!selectedSubtype.value) setError('subtype', '请选择设备子类型。')
  if (selectedSubtype.value?.creation !== 'SUPPORTED') setError('subtype', selectedSubtype.value?.note || '该设备类型暂不支持新增。')
  if (!isEdit.value) {
    if (!form.protocol) setError('protocol', '请选择协议。')
    if (form.port < 1 || form.port > 65535) setError('port', '端口必须在 1-65535 范围内。')
    if (!form.username.trim()) setError('username', '用户名为必填项。')
    if (!/^[A-Z][A-Z0-9_]{0,63}$/.test(form.credentialReference.trim())) setError('credentialReference', '请输入有效的凭据引用，例如 H3C_READER。')
    if (form.protocol === 'SSH' && !form.hostKeySha256.trim()) setError('hostKeySha256', 'SSH 必须填写 SHA256 主机密钥指纹，未知主机不会自动接受。')
  }
  return Object.keys(formErrors).length === 0
}

function connectionPayload(): Record<string, unknown> {
  return {
    id: form.id || undefined,
    name: form.name.trim(), managementIp: form.managementAddress.trim(), vendor: form.vendor,
    model: form.model.trim(), deviceType: selectedSubtype.value?.backendType || '', siteId: form.siteId,
    groupId: form.groupId || null, description: form.description.trim(), enabled: form.enabled,
    protocol: form.protocol, port: form.port, username: form.username.trim(), credentialReference: form.credentialReference.trim(),
    hostKeySha256: form.hostKeySha256.trim(), hostKeyUpdatePolicy: 'STRICT', terminalEncoding: form.terminalEncoding,
    allowLegacyAlgorithms: false, allowTelnetFallback: false, idempotencyKey: idempotencyKey.value,
  }
}

function editPayload(): Record<string, unknown> {
  return {
    id: form.id, name: form.name.trim(), managementIp: form.managementAddress.trim(), vendor: form.vendor,
    model: form.model.trim(), deviceType: selectedSubtype.value?.backendType || '', siteId: form.siteId,
    groupId: form.groupId || null, description: form.description.trim(), enabled: form.enabled,
    terminalProfileId: selectedProfileId.value || null,
  }
}

const selectedProfileId = ref('')

async function save(): Promise<void> {
  if (!validate()) { ElMessage.warning('请先修正页面中的校验项'); return }
  loading.value = true
  try {
    if (isEdit.value) await updateDevice(form.id, editPayload())
    else await createDevice(connectionPayload())
    ElMessage.success(isEdit.value ? '设备信息已保存' : '设备及终端档案已原子创建')
    await router.push('/devices')
  } catch (cause) {
    if (cause instanceof ApiRequestError && cause.status === 409) ElMessage.error(`保存冲突：${cause.message}`)
    else ElMessage.error(cause instanceof Error ? cause.message : '设备保存失败')
  } finally { loading.value = false }
}

async function runConnectivityTest(): Promise<void> {
  clearErrors(); connectivity.value = null
  if (isEdit.value || !validate()) { if (!isEdit.value) ElMessage.warning('请先填写完整连接信息'); return }
  connectivityLoading.value = true
  const controller = new AbortController()
  connectivityController.value = controller
  try {
    connectivity.value = await testDeviceConnectivity({
      managementAddress: form.managementAddress.trim(), protocol: form.protocol, port: form.port,
      username: form.username.trim(), credentialReference: form.credentialReference.trim(),
      hostKeySha256: form.hostKeySha256.trim(), hostKeyUpdatePolicy: 'STRICT', terminalEncoding: form.terminalEncoding,
      timeoutMs: form.timeoutMs,
    }, controller.signal)
  } catch (cause) {
    if ((cause as DOMException)?.name !== 'AbortError') ElMessage.error(cause instanceof Error ? cause.message : '连通性测试失败')
  } finally { connectivityLoading.value = false; connectivityController.value = null }
}
function cancelConnectivityTest(): void { connectivityController.value?.abort() }
function statusClass(value?: string): string { return value === 'SUCCEEDED' ? 'good' : value === 'PARTIAL' ? 'warn' : 'bad' }

async function loadDetail(): Promise<void> {
  if (!isEdit.value) return
  loadingDetail.value = true
  try {
    const detail = await getDeviceInventoryDetail(editingId.value)
    const item = detail.item
    const subtype = subtypeByBackendType(item.deviceType)
    form.id = item.id; form.name = item.name; form.siteId = item.siteId || ''
    form.groupId = item.groupId || ''; form.vendor = item.vendor === '—' ? 'H3C' : item.vendor
    form.model = item.model === '—' ? '' : item.model; form.category = subtype ? 'NETWORK_DEVICE' : 'LEGACY'
    legacySubtype.value = subtype || (item.deviceType ? { key: `LEGACY_${item.deviceType}`, label: `现有类型（${item.deviceType}）`, backendType: item.deviceType, creation: 'SUPPORTED' } : null)
    form.subtype = legacySubtype.value?.key || ''; form.managementAddress = item.managementIp || item.managementAddress
    form.description = detail.description; form.enabled = item.enabled; selectedProfileId.value = detail.terminalProfileId || ''
    if (detail.terminalProfile) {
      form.port = detail.terminalProfile.port; form.username = detail.terminalProfile.username
      form.protocol = detail.terminalProfile.terminalProtocol === 'TELNET' ? 'TELNET' : 'SSH'
      form.terminalEncoding = (detail.terminalProfile.terminalEncoding as 'AUTO' | 'UTF-8' | 'GB18030') || 'AUTO'
    }
    await refreshGroups()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备详情读取失败'); await router.push('/devices') }
  finally { loadingDetail.value = false }
}

watch(() => siteContext.currentSiteId, value => {
  if (!isEdit.value && value && !form.siteId) form.siteId = value
})
watch(() => form.siteId, (value, previous) => {
  if (value && value !== previous) { if (!isEdit.value) form.groupId = ''; void refreshGroups() }
})
onMounted(async () => {
  if (!isEdit.value) form.siteId = siteContext.currentSiteId
  await loadDetail()
  if (!isEdit.value) await refreshGroups()
})
</script>

<template>
  <NcLayout :title="pageTitle" :description="isEdit ? '编辑库存元数据；已有终端档案的连接身份保持只读，避免无意替换历史连接。' : '按局点创建设备库存与受保护终端档案，保存前可手动执行一次受控连通性测试。'">
    <template #actions><el-button size="small" @click="router.push('/devices')">返回设备列表</el-button></template>
    <div v-if="loadingDetail" class="card editor-loading">正在读取设备详情…</div>
    <form v-else class="device-editor" @submit.prevent="save">
      <div v-if="formErrors.form" class="form-error form-error--banner">{{ formErrors.form }}</div>
      <section class="card editor-card">
        <div class="editor-card__header"><div><h2>基本信息</h2><p>设备身份与当前 Site Scope 绑定，设备 ID 由服务端稳定生成。</p></div><span class="required-hint">* 为必填</span></div>
        <div class="editor-card__body form-grid form-grid--wide">
          <label class="form-field"><span>设备名称 <i>*</i></span><input v-model="form.name" placeholder="例如：杭州东站核心交换机" @input="delete formErrors.name" /><small v-if="formErrors.name" class="field-error">{{ formErrors.name }}</small></label>
          <label class="form-field"><span>设备 ID</span><input :value="form.id || '保存后由服务端生成'" disabled class="mono" /></label>
          <label class="form-field"><span>厂商 <i>*</i></span><el-select :model-value="form.vendor" filterable class="full-control" :disabled="connectionReadOnly" @change="chooseVendor"><el-option v-for="vendor in vendorOptions" :key="vendor.key" :label="vendor.label" :value="vendor.key" /></el-select></label>
          <label class="form-field"><span>设备类别 <i>*</i></span><el-select :model-value="form.category" filterable class="full-control" :disabled="connectionReadOnly" @change="chooseCategory"><el-option v-for="category in categories" :key="category.key" :label="category.label" :value="category.key" /></el-select></label>
          <label class="form-field"><span>设备子类型 <i>*</i></span><el-select :model-value="form.subtype" filterable class="full-control" :disabled="connectionReadOnly" placeholder="搜索并选择子类型" @change="chooseSubtype"><el-option v-for="subtype in subtypes" :key="subtype.key" :label="subtype.label" :value="subtype.key" :disabled="subtype.creation !== 'SUPPORTED'"><template #default><span>{{ subtype.label }}</span><small v-if="subtype.creation !== 'SUPPORTED'"> · 需可信 AC 关联</small></template></el-option></el-select><small v-if="formErrors.subtype" class="field-error">{{ formErrors.subtype }}</small></label>
          <label class="form-field"><span>型号</span><input v-model="form.model" placeholder="可选，例如 S5560X" /></label>
          <label class="form-field"><span>授权 Site <i>*</i></span><el-select v-model="form.siteId" filterable class="full-control" :disabled="connectionReadOnly" placeholder="选择有权限的启用 Site"><el-option v-for="site in siteContext.activeSites" :key="site.id" :label="`${site.name} · ${site.id}`" :value="site.id" /></el-select><small v-if="formErrors.siteId" class="field-error">{{ formErrors.siteId }}</small></label>
          <label class="form-field"><span>设备分组</span><el-select v-model="form.groupId" filterable clearable class="full-control" placeholder="未分组"><el-option v-for="group in groups" :key="group.id" :label="group.name" :value="group.id" /></el-select><small>分组来自所选 Site，切换 Site 后会重新读取并清空。</small></label>
          <label class="form-field form-field--full"><span>描述</span><textarea v-model="form.description" rows="3" placeholder="记录设备用途、位置或维护说明" /></label>
          <label class="toggle-field"><input v-model="form.enabled" type="checkbox" /><span><b>启用设备</b><small>启用只影响设备及其终端档案的业务状态，不会自动发起连接。</small></span></label>
        </div>
      </section>

      <section class="card editor-card">
        <div class="editor-card__header"><div><h2>连接信息</h2><p>{{ isEdit ? '连接档案已存在；身份、凭据引用和主机指纹不会在编辑页面回显或替换。' : '连接信息只保存凭据引用，不接收、不落库、不回显明文密码。' }}</p></div><span class="security-badge">受保护边界</span></div>
        <div class="editor-card__body form-grid form-grid--wide">
          <label class="form-field form-field--full"><span>管理 IP / 主机名 <i>*</i></span><input v-model="form.managementAddress" :disabled="connectionReadOnly" placeholder="192.0.2.10 或 device.example.local" /><small v-if="formErrors.managementAddress" class="field-error">{{ formErrors.managementAddress }}</small></label>
          <label class="form-field"><span>协议 <i>*</i></span><el-select :model-value="form.protocol" class="full-control" :disabled="connectionReadOnly" @change="changeProtocol"><el-option label="SSH" value="SSH" /><el-option label="Telnet" value="TELNET" /></el-select><small>仅显示当前网络设备工作流真正支持的协议；COM 串口需要独立参数与本机权限，本页面未启用。</small></label>
          <label class="form-field"><span>端口 <i>*</i></span><el-input-number v-model="form.port" :min="1" :max="65535" :disabled="connectionReadOnly" controls-position="right" class="full-control" /><small v-if="formErrors.port" class="field-error">{{ formErrors.port }}</small></label>
          <label class="form-field"><span>用户名 <i>*</i></span><input v-model="form.username" :disabled="connectionReadOnly" placeholder="例如 operator" /><small v-if="formErrors.username" class="field-error">{{ formErrors.username }}</small></label>
          <label class="form-field"><span>凭据引用 <i>*</i></span><input v-model="form.credentialReference" :disabled="connectionReadOnly" placeholder="例如 H3C_READER" autocomplete="off" /><small v-if="formErrors.credentialReference" class="field-error">{{ formErrors.credentialReference }}</small><small v-else>由本机 Local Manager / DPAPI 凭据库预先配置。</small></label>
          <label v-if="form.protocol === 'SSH'" class="form-field form-field--full"><span>SSH 主机密钥 SHA256 指纹 <i>*</i></span><input v-model="form.hostKeySha256" :disabled="connectionReadOnly" placeholder="SHA256:…" autocomplete="off" /><small v-if="formErrors.hostKeySha256" class="field-error">{{ formErrors.hostKeySha256 }}</small><small v-else>必须人工提供已核验指纹；不会接受未知主机密钥。</small></label>
          <label class="form-field"><span>终端编码</span><el-select v-model="form.terminalEncoding" class="full-control" :disabled="connectionReadOnly"><el-option label="自动" value="AUTO" /><el-option label="UTF-8" value="UTF-8" /><el-option label="GB18030" value="GB18030" /></el-select></label>
          <label v-if="!isEdit" class="form-field"><span>连通性超时（毫秒）</span><el-input-number v-model="form.timeoutMs" :min="1000" :max="60000" :step="1000" controls-position="right" class="full-control" /></label>
          <div class="form-field form-field--full connection-actions"><el-button v-if="!isEdit && !connectivityLoading" type="primary" plain @click="runConnectivityTest">测试连通性</el-button><el-button v-if="!isEdit && connectivityLoading" type="warning" plain @click="cancelConnectivityTest">取消测试</el-button><span class="muted">测试只做地址/端口、协议握手、认证和只读版本识别，不创建会话、不修改设备配置。</span></div>
          <div v-if="connectivity" class="connectivity-result" :class="statusClass(connectivity.outcome)"><div class="connectivity-result__title"><strong>{{ connectivity.outcome === 'SUCCEEDED' ? '连通性测试通过' : connectivity.outcome === 'PARTIAL' ? '连通性测试部分通过' : '连通性测试失败' }}</strong><span>{{ connectivity.elapsedMs }} ms · {{ connectivity.stage }}</span></div><p>{{ connectivity.detail }}</p><div class="connectivity-steps"><span :class="{ done: connectivity.addressReachable }">地址/端口 {{ connectivity.addressReachable ? '可达' : '未通过' }}</span><span :class="{ done: connectivity.protocolHandshake }">协议握手 {{ connectivity.protocolHandshake ? '通过' : '未通过' }}</span><span :class="{ done: connectivity.authentication }">认证 {{ connectivity.authentication ? '通过' : '未通过' }}</span><span :class="{ done: connectivity.identityVerified }">设备身份 {{ connectivity.identityVerified ? '已识别' : '未确认' }}</span></div></div>
        </div>
      </section>

      <section class="card editor-card">
        <button type="button" class="editor-collapse" @click="advancedOpen = advancedOpen.length ? [] : ['advanced']"><span><b>高级文件传输设置</b><small>FTP / TFTP / SFTP / HTTP</small></span><span>{{ advancedOpen.length ? '收起' : '展开' }}</span></button>
        <div v-if="advancedOpen.length" class="editor-unsupported"><strong>当前工作流未启用文件传输能力</strong><p>本版本不会自动启用 FTP、TFTP、SFTP 或 HTTP，也不会保存未实现的开关；需要明确的设备能力、授权和审计契约后再接入。</p></div>
      </section>
      <section class="card editor-card">
        <button type="button" class="editor-collapse" @click="advancedOpen = advancedOpen.includes('proxy') ? [] : ['proxy']"><span><b>代理信息</b><small>远程代理 / 跳板机</small></span><span>{{ advancedOpen.includes('proxy') ? '收起' : '展开' }}</span></button>
        <div v-if="advancedOpen.includes('proxy')" class="editor-unsupported"><strong>当前工作流未启用远程代理</strong><p>不会静默使用未经授权的代理、跳板机或第三方转发；代理配置需要独立的权限边界和连接审计契约。</p></div>
      </section>

      <div class="device-editor-actions"><div><span v-if="isEdit" class="muted">编辑仅更新库存元数据；连接身份保持原 Profile 不变。</span><span v-else class="muted">保存会原子创建库存记录与终端档案；重复提交使用幂等键返回同一结果。</span></div><div><el-button @click="router.push('/devices')">取消</el-button><el-button type="primary" :loading="loading" :disabled="!canWrite" native-type="submit">{{ submitLabel }}</el-button></div></div>
    </form>
  </NcLayout>
</template>

<style scoped>
.device-editor { display: grid; gap: 14px; padding-bottom: 18px; }
.editor-card { overflow: hidden; }
.editor-card__header { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding: 19px 22px 15px; border-bottom: 1px solid var(--nc-border); }
.editor-card__header h2 { margin: 0; font-size: 17px; }
.editor-card__header p { margin: 6px 0 0; color: var(--nc-text-secondary); font-size: 14px; line-height: 1.55; }
.editor-card__body { padding: 21px 22px 23px; }
.required-hint { color: var(--nc-text-muted); font-size: 13px; white-space: nowrap; }
.security-badge { border-radius: 999px; padding: 4px 10px; color: var(--nc-success); background: rgb(22 184 120 / 11%); font-size: 12px; white-space: nowrap; }
.form-grid--wide { gap: 17px 20px; }
.form-field { gap: 7px; color: var(--nc-text-secondary); font-size: 14px; }
.form-field > span { color: var(--nc-text); font-weight: 600; }
.form-field > span i { color: var(--nc-danger); font-style: normal; }
.form-field input, .form-field textarea { min-height: 38px; border: 1px solid var(--nc-border); border-radius: 6px; padding: 7px 10px; color: var(--nc-text); background: var(--nc-surface); outline: none; font-size: 14px; }
.form-field textarea { resize: vertical; line-height: 1.5; }
.form-field input:focus, .form-field textarea:focus { border-color: var(--nc-primary); box-shadow: 0 0 0 2px rgb(18 111 232 / 12%); }
.form-field input:disabled { color: var(--nc-text-secondary); background: var(--nc-surface-muted); }
.full-control { width: 100%; min-height: 38px; }
.form-field small, .toggle-field small { color: var(--nc-text-muted); font-size: 13px; line-height: 1.45; }
.field-error, .form-error { color: var(--nc-danger) !important; }
.form-error--banner { padding: 11px 14px; border: 1px solid rgb(235 83 96 / 25%); border-radius: 7px; background: rgb(235 83 96 / 8%); font-size: 14px; }
.toggle-field { display: flex; align-items: center; gap: 10px; grid-column: 1 / -1; padding-top: 2px; }
.toggle-field input { width: 18px; height: 18px; accent-color: var(--nc-primary); }
.toggle-field span { display: grid; gap: 3px; }
.connection-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; }
.connectivity-result { display: grid; gap: 8px; grid-column: 1 / -1; padding: 14px 16px; border: 1px solid var(--nc-border); border-left: 4px solid var(--nc-warning); border-radius: 7px; background: var(--nc-surface-muted); }
.connectivity-result.good { border-left-color: var(--nc-success); }
.connectivity-result.bad { border-left-color: var(--nc-danger); }
.connectivity-result__title { display: flex; justify-content: space-between; gap: 14px; font-size: 14px; }
.connectivity-result__title span { color: var(--nc-text-muted); font-size: 13px; }
.connectivity-result p { margin: 0; color: var(--nc-text-secondary); font-size: 14px; line-height: 1.5; }
.connectivity-steps { display: flex; flex-wrap: wrap; gap: 8px; }
.connectivity-steps span { border-radius: 999px; padding: 4px 9px; color: var(--nc-text-muted); background: var(--nc-surface); font-size: 13px; }
.connectivity-steps span.done { color: var(--nc-success); background: rgb(22 184 120 / 10%); }
.editor-collapse { width: 100%; display: flex; align-items: center; justify-content: space-between; border: 0; padding: 17px 22px; color: var(--nc-text); background: var(--nc-surface); text-align: left; }
.editor-collapse span:first-child { display: grid; gap: 4px; }
.editor-collapse b { font-size: 16px; }
.editor-collapse small { color: var(--nc-text-secondary); font-size: 13px; }
.editor-collapse > span:last-child { color: var(--nc-primary); font-size: 13px; }
.editor-unsupported { margin: 0 22px 20px; padding: 14px 16px; border: 1px dashed var(--nc-border); border-radius: 7px; color: var(--nc-text-secondary); background: var(--nc-surface-muted); font-size: 14px; line-height: 1.6; }
.editor-unsupported strong { color: var(--nc-text); }
.editor-unsupported p { margin: 5px 0 0; }
.device-editor-actions { position: sticky; bottom: -1px; z-index: 2; display: flex; align-items: center; justify-content: space-between; gap: 18px; padding: 13px 16px; border: 1px solid var(--nc-border); border-radius: 8px; background: color-mix(in srgb, var(--nc-surface) 95%, transparent); box-shadow: 0 -5px 15px rgb(18 44 79 / 7%); }
.device-editor-actions > div:first-child { min-width: 0; font-size: 13px; }
.device-editor-actions > div:last-child { display: flex; gap: 8px; flex: 0 0 auto; }
.editor-loading { padding: 28px; color: var(--nc-text-secondary); font-size: 14px; }
@media (max-width: 1250px) { .editor-card__header, .editor-card__body { padding-left: 17px; padding-right: 17px; } .editor-unsupported { margin-left: 17px; margin-right: 17px; } .device-editor-actions { align-items: flex-start; flex-direction: column; } }
</style>
