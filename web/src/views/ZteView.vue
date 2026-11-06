<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { createZteProfile, getDeviceSites, getZteFacts, getZteProfiles, getZteRun, startZteCollection, updateZteCredential, type DeviceSite, type ZteCollectionRun, type ZteFact, type ZteProfile } from '../api/client'
import NcLayout from '../components/NcLayout.vue'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const profiles = ref<ZteProfile[]>([])
const sites = ref<DeviceSite[]>([])
const selected = ref<ZteProfile | null>(null)
const run = ref<ZteCollectionRun | null>(null)
const facts = ref<ZteFact[]>([])
const loading = ref(false)
const saving = ref(false)
const showCreate = ref(false)
const credentialProfile = ref<ZteProfile | null>(null)
const credentialPassword = ref('')
const clearPassword = ref(false)
const form = reactive({ host: '', port: 22, username: '', password: '', siteId: 'local' })
let selectionGeneration = 0
const payload = (fact: ZteFact): unknown => { try { return JSON.parse(fact.payloadJson) } catch { return fact.payloadJson } }
const formatTime = (value?: number | null): string => value ? new Date(value).toLocaleString() : '—'
async function refresh(): Promise<void> {
  profiles.value = await getZteProfiles()
  if (selected.value) selected.value = profiles.value.find(profile => profile.id === selected.value?.id) || null
  if (!selected.value && profiles.value.length) await selectProfile(profiles.value[0])
  if (selected.value) facts.value = await getZteFacts(selected.value.id, { history: false, limit: 50 })
}
async function openCreate(): Promise<void> {
  if (!auth.isSuperAdmin) return
  try {
    sites.value = await getDeviceSites()
    Object.assign(form, { host: '', port: 22, username: '', password: '', siteId: sites.value[0]?.id || 'local' })
    showCreate.value = true
  } catch { ElMessage.error('局点读取失败') }
}
async function saveProfile(): Promise<void> {
  if (!auth.isSuperAdmin) return
  if (!form.host.trim() || !form.username.trim() || !form.password || !form.siteId) {
    ElMessage.warning('请填写设备地址、用户名、密码并选择局点'); return
  }
  if (!Number.isInteger(form.port) || form.port < 1 || form.port > 65535) {
    ElMessage.warning('端口必须介于 1 和 65535'); return
  }
  saving.value = true
  try {
    const profile = await createZteProfile({ id: crypto.randomUUID().replaceAll('-', ''), host: form.host.trim(), port: form.port, username: form.username.trim(), password: form.password, siteId: form.siteId, hostKeyUpdatePolicy: 'AUTO_UPDATE' })
    form.password = ''
    showCreate.value = false
    await refresh()
    await selectProfile(profile)
    ElMessage.success('ZTE 连接档案已保存，后续采集自动登录')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'ZTE 连接档案保存失败') }
  finally { saving.value = false }
}
function openCredential(profile: ZteProfile): void {
  credentialPassword.value = ''
  clearPassword.value = false
  credentialProfile.value = profile
}
function setCredentialDialog(value: boolean): void { if (!value) credentialProfile.value = null }
async function saveCredential(): Promise<void> {
  if (!auth.isSuperAdmin || !credentialProfile.value) return
  saving.value = true
  try {
    await updateZteCredential(credentialProfile.value.id, { password: clearPassword.value ? undefined : credentialPassword.value || undefined, clearPassword: clearPassword.value })
    credentialPassword.value = ''
    credentialProfile.value = null
    ElMessage.success('ZTE 设备密码已更新')
    await refresh()
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'ZTE 设备密码更新失败') }
  finally { saving.value = false }
}
async function selectProfile(profile: ZteProfile): Promise<void> {
  selectionGeneration += 1
  const generation = selectionGeneration
  selected.value = profile
  run.value = null
  const nextFacts = await getZteFacts(profile.id, { history: false, limit: 50 })
  if (generation === selectionGeneration) facts.value = nextFacts
}
async function collect(profile: ZteProfile): Promise<void> {
  loading.value = true
  try {
    await selectProfile(profile)
    const generation = selectionGeneration
    const result = await startZteCollection(profile.id)
    let currentRun = await getZteRun(result.runId)
    if (generation === selectionGeneration) run.value = currentRun
    for (let i = 0; i < 1200 && currentRun && ['QUEUED', 'RUNNING'].includes(currentRun.state); i++) {
      await new Promise(resolve => setTimeout(resolve, 500))
      currentRun = await getZteRun(result.runId)
      if (generation === selectionGeneration) run.value = currentRun
      if (generation !== selectionGeneration) break
    }
    if (generation === selectionGeneration) {
      facts.value = await getZteFacts(profile.id, { limit: 50 })
      ElMessage.success(currentRun?.state === 'SUCCEEDED' ? 'ZTE 采集完成' : `ZTE 采集状态：${currentRun?.state || 'UNKNOWN'}`)
    }
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'ZTE 采集失败') }
  finally { loading.value = false }
}
onMounted(() => { void refresh().catch(() => ElMessage.error('ZTE profile 读取失败')) })
</script>

<template>
  <NcLayout title="ZTE 采集" description="只读 ZXR10 profile、接口、光模块与 LLDP 当前快照；失败采集不会覆盖上一份有效数据">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button v-if="auth.isSuperAdmin" type="primary" size="small" @click="openCreate">新增 ZTE 设备</el-button></template>
    <section class="card"><div class="card__body"><div v-if="!profiles.length" class="empty-state"><strong>暂无 ZTE 设备</strong><small>新增设备并保存密码后，采集自动登录并记录 SSH 指纹。</small></div><div v-for="profile in profiles" :key="profile.id" class="overview-footer" :class="{ 'is-selected': selected?.id === profile.id }"><span><b>{{ profile.host }}</b> · {{ profile.username }} · {{ profile.siteId }}</span><span><span class="state-pill" :class="profile.credentialConfigured ? 'good' : 'neutral'">{{ profile.credentialConfigured ? '已保存密码' : '未保存密码' }}</span><span class="state-pill" :class="profile.enabled ? 'good' : 'neutral'">{{ profile.enabled ? '已启用' : '已禁用' }}</span><el-button size="small" @click="selectProfile(profile)">查看</el-button><el-button v-if="auth.isSuperAdmin" size="small" @click="openCredential(profile)">更新密码</el-button><el-button v-if="auth.isSuperAdmin" size="small" :disabled="!profile.enabled" :loading="loading && selected?.id === profile.id" @click="collect(profile)">采集</el-button></span></div></div></section>
    <section v-if="selected" class="card"><div class="card__body"><div class="inventory-summary"><span class="summary-chip"><b>{{ selected.host }}</b></span><span class="summary-chip">最近运行：{{ run?.state || '—' }}</span><span class="summary-chip">{{ formatTime(run?.endedMs || run?.startedMs) }}</span></div><p v-if="run?.failureReason" class="data-quality-note">采集失败：{{ run.failureReason }}；当前事实保持不变。</p><div class="table-wrap"><table class="data-table"><thead><tr><th>事实</th><th>状态</th><th>采集时间</th><th>数据</th></tr></thead><tbody><tr v-for="fact in facts" :key="fact.kind"><td>{{ fact.kind }}</td><td>{{ fact.status }}</td><td>{{ formatTime(fact.capturedMs) }}</td><td><pre class="mono">{{ JSON.stringify(payload(fact), null, 2) }}</pre></td></tr><tr v-if="!facts.length"><td colspan="4" class="muted">暂无当前事实</td></tr></tbody></table></div></div></section>
    <el-dialog v-model="showCreate" title="新增 ZTE 设备" width="560px" @closed="form.password = ''"><form class="form-grid" @submit.prevent="saveProfile"><label class="form-field"><span>设备地址</span><input v-model="form.host" required /></label><label class="form-field"><span>SSH 端口</span><input v-model.number="form.port" type="number" min="1" max="65535" required /></label><label class="form-field"><span>用户名</span><input v-model="form.username" autocomplete="username" required /></label><label class="form-field"><span>密码</span><input v-model="form.password" type="password" autocomplete="new-password" required /></label><label class="form-field"><span>局点</span><select v-model="form.siteId" required><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.name }}</option></select></label><p class="form-hint form-field--full">保存后自动使用设备密码，首次连接及指纹变化自动记录；采集仅执行只读命令。</p><div class="form-field form-field--full"><el-button type="primary" native-type="submit" :loading="saving">保存设备</el-button></div></form></el-dialog>
    <el-dialog :model-value="credentialProfile !== null" title="更新 ZTE 设备密码" width="480px" @update:model-value="setCredentialDialog" @closed="credentialPassword = ''"><p>{{ credentialProfile?.host }} · {{ credentialProfile?.username }}</p><label class="form-field"><span>新密码</span><input v-model="credentialPassword" type="password" autocomplete="new-password" :disabled="clearPassword" /><small class="form-hint">留空保留已保存的密码。</small></label><label class="form-field"><span><input v-model="clearPassword" type="checkbox" /> 清除已保存密码</span></label><template #footer><el-button @click="credentialProfile = null">取消</el-button><el-button type="primary" :loading="saving" @click="saveCredential">保存密码</el-button></template></el-dialog>
  </NcLayout>
</template>
