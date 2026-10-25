<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { CircleCheck, DataLine, Document, Filter, Monitor, Search, Warning } from '@element-plus/icons-vue'
import NcLayout from '../components/NcLayout.vue'
import { ApiRequestError, createH3cProfile, getDeviceInventory, getHostTools, launchHostTool, type DeviceInventoryPage, type HostTool, type TerminalDevice } from '../api/client'
import { displayValue, normalizeDevice, type DeviceRow } from '../deviceViewModel'
import { useAuthStore } from '../stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const result = ref<DeviceInventoryPage | null>(null)
const loading = ref(false)
const unsupported = ref(false)
const page = ref(1)
const pageSize = ref(20)
const search = ref(String(route.query.q || ''))
const status = ref('')
const showCreate = ref(false)
const selected = ref<DeviceRow | null>(null)
const detailDialog = ref(false)
const form = reactive({ id: '', host: '', port: 22, username: '', credentialReference: '', hostKeySha256: '', enabled: true })
const externalDialog = ref(false)
const externalDevice = ref<TerminalDevice | null>(null)
const externalTools = ref<HostTool[]>([])
const selectedTool = ref('')
const rows = computed(() => (result.value?.items || []).map(normalizeDevice))
const summary = computed(() => result.value?.summary || { totalRecords: 0, validDevices: 0, invalidRecords: 0, enabledDevices: 0, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 0 })

async function refresh(): Promise<void> {
  loading.value = true; unsupported.value = false
  try { result.value = await getDeviceInventory({ page: page.value, pageSize: pageSize.value, search: search.value, status: status.value }) }
  catch (cause) { unsupported.value = cause instanceof ApiRequestError && [404, 503].includes(cause.status); if (!unsupported.value) ElMessage.error('设备库存 API 读取失败') }
  finally { loading.value = false }
}
function applyFilters(): void { page.value = 1; void refresh() }
function clearFilters(): void { search.value = ''; status.value = ''; applyFilters() }
function changePage(next: number): void { page.value = next; void refresh() }
function changePageSize(): void { page.value = 1; void refresh() }
function statusLabel(value: string): string { return ({ CONNECTED: '最近在线', DISCONNECTED: '最近离线', FAILED: '会话失败', CLOSED: '会话关闭', DISABLED: '已禁用', UNKNOWN: '未知' } as Record<string, string>)[value] || '未知' }
function statusClass(value: string): string { return value === 'CONNECTED' ? 'good' : value === 'DISABLED' || value === 'FAILED' ? 'bad' : value === 'UNKNOWN' ? 'neutral' : 'warn' }
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function openDetails(device: DeviceRow): void { selected.value = device; detailDialog.value = true }
function openInner(device: DeviceRow): void { router.push({ path: '/terminal', query: { deviceId: device.id } }) }
async function create(): Promise<void> {
  try {
    await createH3cProfile({ ...form })
    ElMessage.success('设备已登记'); showCreate.value = false
    Object.assign(form, { id: '', host: '', port: 22, username: '', credentialReference: '', hostKeySha256: '', enabled: true })
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备登记失败') }
}
async function openExternal(device: DeviceRow): Promise<void> {
  if (auth.user?.role !== 'SUPERADMIN') { ElMessage.warning('只有 SUPERADMIN 可以启动宿主机外部终端'); return }
  externalDevice.value = { id: device.id, host: device.address.split(':')[0], port: Number(device.address.split(':').pop()) || 22, username: device.username, enabled: device.enabled, supportedProtocols: [device.protocol], hostKeyUpdatePolicy: 'STRICT', allowLegacyAlgorithms: false, terminalEncoding: 'AUTO', terminalProtocol: device.protocol, allowTelnetFallback: false }
  try { externalTools.value = await getHostTools(); selectedTool.value = externalTools.value.find(tool => tool.status === 'READY')?.id || externalTools.value[0]?.id || ''; externalDialog.value = true }
  catch { ElMessage.error('宿主机工具列表读取失败') }
}
async function launchExternal(): Promise<void> {
  if (!externalDevice.value || !selectedTool.value) return
  try { const output = await launchHostTool(selectedTool.value, externalDevice.value.id); ElMessage[output.status === 'LAUNCHED' ? 'success' : 'warning'](output.detail || output.status); externalDialog.value = false }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '外部终端启动失败') }
}
watch(() => route.query.q, value => { if (typeof value === 'string' && value !== search.value) { search.value = value; applyFilters() } })
onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="设备管理" description="以服务端 Catalog 为唯一登记来源，区分登记、凭据与真实会话状态">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button type="primary" size="small" @click="showCreate = true">登记设备</el-button></template>

    <div v-if="unsupported" class="card"><div class="empty-state"><div><el-icon><Monitor /></el-icon><strong>设备库存 API 尚未启用</strong><small>当前服务未提供设备查询能力；页面不会使用前端模拟设备填充列表。</small></div></div></div>
    <template v-else>
      <div class="inventory-summary">
        <span class="summary-chip"><b>{{ summary.totalRecords }}</b> 条登记记录</span>
        <span class="summary-chip summary-chip--success"><b>{{ summary.validDevices }}</b> 条结构有效</span>
        <span class="summary-chip summary-chip--danger"><b>{{ summary.invalidRecords }}</b> 条数据异常</span>
        <span class="summary-chip summary-chip--info"><b>{{ summary.enabledDevices }}</b> 已启用</span>
        <span class="summary-chip"><b>{{ summary.disabledDevices }}</b> 已禁用</span>
        <span class="summary-chip"><b>{{ summary.onlineDevices }}</b> 最近在线</span>
        <span class="summary-chip"><b>{{ summary.unknownStatus }}</b> 状态未知</span>
      </div>
      <div v-if="summary.invalidRecords" class="data-quality-note"><el-icon><Warning /></el-icon><span>服务端报告 {{ summary.invalidRecords }} 条结构异常记录。异常记录保留在分页结果中，并显示缺失字段，不通过隐藏数据来修饰统计。</span></div>
      <section class="card">
        <div class="filter-bar">
          <label class="global-search filter-input"><el-icon><Search /></el-icon><input v-model="search" placeholder="搜索设备 ID、地址或账号" @keydown.enter="applyFilters" /></label>
          <select v-model="status" aria-label="设备状态" @change="applyFilters"><option value="">全部状态</option><option value="ENABLED">已启用</option><option value="DISABLED">已禁用</option><option value="ONLINE">最近在线</option><option value="OFFLINE">最近离线</option><option value="UNKNOWN">状态未知</option></select>
          <button class="icon-button" type="button" title="清除筛选" @click="clearFilters"><el-icon><Filter /></el-icon></button>
          <span class="filter-bar__grow" />
          <span class="muted">分页读取，不一次性渲染全部记录</span>
        </div>
        <div v-loading="loading" class="table-wrap">
          <table class="data-table"><thead><tr><th>状态</th><th>设备名称 / ID</th><th>管理地址</th><th>设备类型</th><th>协议</th><th>局点 / 分组</th><th>凭据</th><th>最近会话</th><th>操作</th></tr></thead>
            <tbody>
              <tr v-for="device in rows" :key="device.id" :class="{ 'is-invalid': !device.isValid }" @dblclick="openDetails(device)">
                <td><span :class="['state-pill', statusClass(device.status)]"><i class="status-dot" />{{ statusLabel(device.status) }}</span></td>
                <td><span class="device-name">{{ device.name }}</span><span class="device-subline mono">{{ device.id }}<span v-if="!device.isValid" class="device-quality">· {{ device.qualityIssues.join(' / ') }}</span></span></td>
                <td class="device-address">{{ device.address }}<span class="device-subline">{{ device.username }}</span></td>
                <td>{{ device.type }}</td><td>{{ device.protocol }}</td><td>{{ device.site }}<span class="device-subline">{{ device.group }}</span></td>
                <td><span :class="['state-pill', device.credentialConfigured ? 'good' : 'warn']">{{ device.credentialConfigured ? '已配置' : '未配置' }}</span></td>
                <td>{{ formatTime(device.lastSeenMs) }}</td>
                <td><div class="table-actions"><el-button text size="small" @click="openDetails(device)">详情</el-button><el-button text size="small" :disabled="device.id === '—'" @click="openInner(device)">终端</el-button><el-button v-if="auth.user?.role === 'SUPERADMIN'" text size="small" @click="openExternal(device)">外部</el-button></div></td>
              </tr>
              <tr v-if="!rows.length"><td colspan="9"><div class="empty-state"><div><el-icon><Document /></el-icon><strong>{{ search || status ? '没有符合条件的设备' : '暂无登记设备' }}</strong><small>{{ search || status ? '尝试清除筛选，或确认服务端 Catalog 是否有数据。' : '服务端当前没有登记设备；不生成模拟设备，也不把空状态伪装成在线。' }}</small></div></div></td></tr>
            </tbody>
          </table>
        </div>
        <div class="pagination-bar"><span>第 {{ page }} 页 · 每页 {{ pageSize }} 条 · 共 {{ result?.total || 0 }} 条</span><div class="toolbar"><select v-model.number="pageSize" @change="changePageSize"><option :value="20">20 / 页</option><option :value="50">50 / 页</option><option :value="100">100 / 页</option></select><el-button size="small" :disabled="page <= 1" @click="changePage(page - 1)">上一页</el-button><el-button size="small" :disabled="!result?.hasMore" @click="changePage(page + 1)">下一页</el-button></div></div>
      </section>
    </template>

    <div class="section-label">数据口径</div>
    <div class="card card--flat"><div class="card__body overview-footer"><span><el-icon><CircleCheck /></el-icon>总数 = h3c_device_profiles 实际记录数</span><span><el-icon><CircleCheck /></el-icon>在线 = 最近真实终端会话 CONNECTED</span><span><el-icon><Warning /></el-icon>启用不等于在线；无会话显示未知</span></div></div>

    <el-dialog v-model="detailDialog" title="设备详情" width="560px"><template v-if="selected"><div class="detail-grid"><div><span>设备名称</span><b>{{ displayValue(selected.name) }}</b></div><div><span>管理地址</span><b class="mono">{{ selected.address }}</b></div><div><span>厂商 / 型号</span><b>{{ selected.vendor }} / {{ selected.model }}</b></div><div><span>协议</span><b>{{ selected.protocol }}</b></div><div><span>局点 / 分组</span><b>{{ selected.site }} / {{ selected.group }}</b></div><div><span>连接状态</span><b>{{ statusLabel(selected.status) }}</b></div><div><span>Host Key</span><b>{{ selected.qualityIssues.includes('MISSING_ID') ? '不可用' : selected.status === 'UNKNOWN' ? '未验证' : '由服务端管理' }}</b></div><div><span>断开原因</span><b>{{ displayValue(selected.lastDisconnectReason) }}</b></div></div><div v-if="!selected.isValid" class="data-quality-note"><el-icon><Warning /></el-icon>{{ selected.qualityIssues.join(' / ') }}</div></template></el-dialog>
    <el-dialog v-model="showCreate" title="登记 H3C 设备" width="620px"><div class="form-grid"><label class="form-field"><span>设备 ID</span><input v-model="form.id" placeholder="例如 site-a-router-01" /></label><label class="form-field"><span>主机 / IP</span><input v-model="form.host" placeholder="192.0.2.10" /></label><label class="form-field"><span>端口</span><input v-model.number="form.port" type="number" min="1" max="65535" /></label><label class="form-field"><span>用户名</span><input v-model="form.username" /></label><label class="form-field"><span>凭据引用</span><input v-model="form.credentialReference" placeholder="Windows DPAPI reference" /></label><label class="form-field"><span>Host Key SHA-256</span><input v-model="form.hostKeySha256" placeholder="SHA256:…" /></label></div><template #footer><el-button @click="showCreate = false">取消</el-button><el-button type="primary" @click="create">保存登记</el-button></template></el-dialog>
    <el-dialog v-model="externalDialog" title="启动宿主机外部终端" width="480px"><div v-if="externalDevice" class="card card--flat" style="padding:12px;margin-bottom:12px">{{ externalDevice.host }}:{{ externalDevice.port }} · {{ externalDevice.username }}</div><label class="form-field"><span>工具</span><select v-model="selectedTool"><option v-for="tool in externalTools" :key="tool.id" :value="tool.id">{{ tool.displayName }} · {{ tool.status }}</option></select></label><template #footer><el-button @click="externalDialog = false">取消</el-button><el-button type="primary" :disabled="!selectedTool" @click="launchExternal">请求启动</el-button></template></el-dialog>
  </NcLayout>
</template>
