<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { ApiRequestError, createConfigBackup, downloadConfigBackup, getConfigBackupBatch, getConfigBackups, getDeviceInventoryDetail, getH3cProfiles, getConfigBackupDiff, getModuleStates, type ConfigBackupBatch, type ConfigBackupDiff, type ConfigBackupKind, type ConfigBackupSnapshot, type DeviceInventoryItem, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const route = useRoute()
const profiles = ref<TerminalDevice[]>([])
const selected = ref<string[]>([])
const profileId = ref('')
const configurationKind = ref<ConfigBackupKind>('RUNNING')
const history = ref<ConfigBackupSnapshot[]>([])
const batch = ref<ConfigBackupBatch | null>(null)
const diff = ref<ConfigBackupDiff | null>(null)
const loading = ref(false)
const comparing = ref(false)
const historyLoading = ref(false)
const unavailable = ref('')
const moduleAvailable = ref(false)
const device = ref<DeviceInventoryItem | null>(null)
const baseId = ref('')
const targetId = ref('')
const isSuperAdmin = computed(() => auth.user?.role === 'SUPERADMIN')
const selectedProfile = computed(() => profiles.value.find(item => item.id === profileId.value))
const hasDeviceContext = computed(() => Object.prototype.hasOwnProperty.call(route.query, 'deviceId'))
const deviceLabel = computed(() => device.value ? `${device.value.name} · ${device.value.managementIp || device.value.managementAddress}` : selectedProfile.value?.host || '')
const canCompare = computed(() => !!baseId.value && baseId.value !== targetId.value &&
  [baseId.value, targetId.value].every(id => history.value.some(item => item.id === id && kindOf(item) === configurationKind.value)))
const batchFailure = computed(() => {
  const failed = batch.value?.items?.find(item => item.state === 'FAILED')
  if (!failed) return ''
  return failed.error === 'CONFIG_BACKUP_SAVED_UNAVAILABLE' ? '设备没有可读取的保存配置，未生成备份。' :
    failed.error === 'CONFIG_BACKUP_OUTPUT_INCOMPLETE' ? '配置命令未返回完整有效内容，未生成备份。' : '部分设备未完成备份，请在任务中心查看原因。'
})
let contextSequence = 0; let historySequence = 0; let diffSequence = 0; let backupSequence = 0
let active = true

function time(value?: number | null): string { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function size(value: number): string { return (value / 1024).toFixed(1) + ' KB' }
function kindOf(snapshot: ConfigBackupSnapshot): ConfigBackupKind { return snapshot.configurationKind ?? 'RUNNING' }
function kindLabel(kind: ConfigBackupKind): string { return { RUNNING: '运行配置', SAVED: '保存配置', STARTUP_INFO: '启动配置指向' }[kind] }
function saveBlob(blob: Blob, name: string): void { const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = name; link.click(); URL.revokeObjectURL(link.href) }
function clearHistory(): void {
  ++historySequence; ++diffSequence
  history.value = []; diff.value = null; baseId.value = ''; targetId.value = ''; historyLoading.value = false; comparing.value = false
}
function clearTarget(): void {
  clearHistory(); ++backupSequence
  profiles.value = []; selected.value = []; profileId.value = ''; device.value = null; batch.value = null; moduleAvailable.value = false; loading.value = false
}
function accessMessage(cause: unknown, fallback: string): string {
  return cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status) ? '设备不存在或不在当前授权范围内' : fallback
}
function reportOperationError(cause: unknown, fallback: string): void {
  const unsupported = cause instanceof ApiRequestError && cause.status === 409 && cause.message.includes('CONFIG_BACKUP_TARGET_UNSUPPORTED')
  const message = unsupported ? '当前配置备份仅支持 H3C 设备' : accessMessage(cause, cause instanceof Error ? cause.message : fallback)
  if (unsupported || (cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status))) clearTarget()
  unavailable.value = message
  ElMessage.error(message)
}
async function loadHistory(): Promise<void> {
  clearHistory()
  const sequence = historySequence; const context = contextSequence; const target = profileId.value; const kind = configurationKind.value
  if (!target || !moduleAvailable.value) return
  historyLoading.value = true
  try {
    const next = await getConfigBackups(target, kind)
    if (sequence !== historySequence || context !== contextSequence) return
    const visible = next.filter(item => kindOf(item) === kind)
    history.value = visible; baseId.value = visible[1]?.id || ''; targetId.value = visible[0]?.id || ''
  } catch (cause) {
    if (sequence === historySequence && context === contextSequence) {
      unavailable.value = accessMessage(cause, '配置备份历史读取失败，请刷新重试')
      if (cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status)) clearTarget()
    }
  } finally { if (sequence === historySequence) historyLoading.value = false }
}
function changeProfile(): void { ++backupSequence; batch.value = null; unavailable.value = ''; void loadHistory() }
function changeKind(): void { ++backupSequence; batch.value = null; unavailable.value = ''; void loadHistory() }
async function refresh(): Promise<void> {
  const context = ++contextSequence
  const contextual = hasDeviceContext.value; const requestedId = route.query.deviceId
  const previousProfileId = profileId.value
  clearTarget(); unavailable.value = ''; loading.value = true
  try {
    if (contextual && (typeof requestedId !== 'string' || !requestedId.trim())) throw new Error('设备链接无效，请从设备管理重新进入')
    const modules = await getModuleStates()
    if (context !== contextSequence) return
    if (!modules.some(item => item.key === 'config-backups' && item.enabled)) throw new Error('配置备份模块未启用')
    let linkedProfileId = ''
    let targetDevice: DeviceInventoryItem | null = null
    if (contextual) {
      const detail = await getDeviceInventoryDetail(requestedId as string)
      if (context !== contextSequence) return
      if (detail.item.id !== requestedId) throw new Error('设备关联不一致，请从设备管理重新进入')
      if (detail.item.vendor.trim().toLowerCase() !== 'h3c' || detail.item.deviceType.toUpperCase() === 'FIT_AP') throw new Error('当前仅支持独立管理的 H3C 设备配置备份')
      linkedProfileId = detail.terminalProfileId || ''
      if (!linkedProfileId || detail.terminalProfile?.id !== linkedProfileId || (detail.item.terminalProfileId && detail.item.terminalProfileId !== linkedProfileId)) throw new Error('该设备尚未关联可用的连接资料')
      targetDevice = detail.item
    }
    const next = await getH3cProfiles()
    if (context !== contextSequence) return
    if (contextual && !next.some(item => item.id === linkedProfileId)) throw new Error('该设备的连接资料不可用或不在当前授权范围内')
    profiles.value = contextual ? next.filter(item => item.id === linkedProfileId) : next
    device.value = targetDevice; moduleAvailable.value = true
    profileId.value = contextual ? linkedProfileId : next.find(item => item.id === previousProfileId)?.id || next[0]?.id || ''
    selected.value = contextual ? [linkedProfileId] : []
    void loadHistory()
  } catch (cause) {
    if (context !== contextSequence) return
    clearTarget()
    unavailable.value = accessMessage(cause, cause instanceof Error ? cause.message : '设备连接资料读取失败，请刷新重试')
  } finally { if (context === contextSequence) loading.value = false }
}
async function startBackup(): Promise<void> {
  if (loading.value || !moduleAvailable.value || !selected.value.length || !isSuperAdmin.value || selected.value.some(id => !profiles.value.some(item => item.id === id))) return
  const context = contextSequence; const sequence = ++backupSequence
  loading.value = true; batch.value = null
  try {
    let next = await createConfigBackup([...selected.value], configurationKind.value)
    if (context !== contextSequence || sequence !== backupSequence) return
    batch.value = next
    for (let index = 0; index < 1200 && next.state === 'RUNNING'; index++) {
      await new Promise(resolve => setTimeout(resolve, 250))
      if (context !== contextSequence || sequence !== backupSequence) return
      next = await getConfigBackupBatch(next.id)
      if (context !== contextSequence || sequence !== backupSequence) return
      batch.value = next
    }
    await loadHistory()
    if (context === contextSequence && sequence === backupSequence) {
      if (next.state === 'RUNNING') ElMessage.info('配置备份仍在运行，可稍后刷新')
      else ElMessage.success(next.state === 'SUCCEEDED' ? '配置备份完成' : '配置备份部分完成')
    }
  } catch (cause) { if (context === contextSequence && sequence === backupSequence) reportOperationError(cause, '配置备份失败，请刷新重试') }
  finally { if (context === contextSequence && sequence === backupSequence) loading.value = false }
}
async function download(snapshot: ConfigBackupSnapshot): Promise<void> {
  const context = contextSequence; const sequence = historySequence
  if (!moduleAvailable.value || !history.value.some(item => item.id === snapshot.id)) return
  try {
    const blob = await downloadConfigBackup(snapshot.id)
    if (context === contextSequence && sequence === historySequence) saveBlob(blob, snapshot.downloadName || ('config-' + kindOf(snapshot).toLowerCase() + '-v' + snapshot.version + '.cfg'))
  } catch (cause) { if (context === contextSequence && sequence === historySequence) reportOperationError(cause, '配置备份下载失败') }
}
async function compare(): Promise<void> {
  if (!moduleAvailable.value || !canCompare.value) return
  const sequence = ++diffSequence; const context = contextSequence
  comparing.value = true; diff.value = null
  try {
    const next = await getConfigBackupDiff(baseId.value, targetId.value)
    if (sequence === diffSequence && context === contextSequence) diff.value = next
  } catch (cause) { if (sequence === diffSequence && context === contextSequence) reportOperationError(cause, '配置差异读取失败') }
  finally { if (sequence === diffSequence) comparing.value = false }
}
onMounted(() => { void refresh() })
watch(() => route.query.deviceId, () => { if (active && route.path === '/config-backups') void refresh() }, { flush: 'sync' })
watch([baseId, targetId], () => { ++diffSequence; diff.value = null; comparing.value = false }, { flush: 'sync' })
onDeactivated(() => { active = false; ++contextSequence; clearTarget(); loading.value = false })
onActivated(() => { if (!active) { active = true; void refresh() } })
onBeforeUnmount(() => { ++contextSequence; clearTarget() })
</script>

<template>
  <NcLayout title="配置备份" description="配置按类别独立保留版本；备份内容先做敏感字段脱敏，下载和差异均重新校验 Site 权限">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button></template>
    <section v-if="!isSuperAdmin" class="data-quality-note">当前角色不能执行配置备份；页面仍可显示已有授权历史。</section>
    <section v-if="unavailable" class="data-quality-note" role="status">{{ unavailable }}</section>
    <section v-if="moduleAvailable" class="card">
      <div class="card__header"><h2>{{ hasDeviceContext ? '当前设备' : '选择设备' }}</h2><span class="state-pill">{{ selected.length }} 台</span></div>
      <div class="card__body">
        <div class="form-grid"><label v-for="profile in profiles" :key="profile.id" class="check-row"><input v-model="selected" type="checkbox" :value="profile.id" :disabled="!isSuperAdmin || loading" />{{ device ? deviceLabel : profile.host + ' · ' + profile.username }}</label></div>
        <div class="toolbar" style="margin-top:12px">
          <label class="form-field"><span>配置类别</span><select v-model="configurationKind" aria-label="配置类别" :disabled="loading" @change="changeKind"><option value="RUNNING">运行配置</option><option value="SAVED">保存配置</option><option value="STARTUP_INFO">启动配置指向</option></select></label>
          <el-button type="primary" :disabled="!selected.length || !isSuperAdmin || loading" :loading="loading" @click="startBackup">开始配置备份</el-button>
          <span v-if="batch" class="muted">{{ kindLabel(batch.configurationKind || 'RUNNING') }} · 批次 {{ batch.succeeded }}/{{ batch.total }} · {{ batch.state }}</span>
        </div>
        <p class="muted">仅查询设备配置；版本历史和差异限同一类别。</p>
        <p v-if="configurationKind === 'STARTUP_INFO'" class="data-quality-note">启动配置指向仅记录设备报告的启动文件信息，不包含文件正文，也不表示可以恢复配置。</p>
        <p v-if="batchFailure" class="data-quality-note" role="status">{{ batchFailure }}</p>
      </div>
    </section>
    <section v-if="selectedProfile && moduleAvailable" class="card">
      <div class="card__header"><div><h2>{{ deviceLabel }} · {{ kindLabel(configurationKind) }}版本历史</h2><small>仅显示当前 Site 可见的完整记录；各类别独立编号</small></div><select v-if="!hasDeviceContext" v-model="profileId" aria-label="备份设备" :disabled="loading" @change="changeProfile"><option v-for="profile in profiles" :key="profile.id" :value="profile.id">{{ profile.host }}</option></select></div>
      <div class="card__body table-wrap" :aria-busy="historyLoading">
        <table class="data-table"><thead><tr><th>类别</th><th>版本</th><th>采集时间</th><th>大小</th><th>SHA-256</th><th>操作</th></tr></thead><tbody><tr v-for="snapshot in history" :key="snapshot.id"><td>{{ kindLabel(kindOf(snapshot)) }}</td><td>v{{ snapshot.version }}</td><td>{{ time(snapshot.capturedMs) }}</td><td>{{ size(snapshot.byteCount) }}</td><td class="mono">{{ snapshot.sha256?.slice(0, 16) }}…</td><td><el-button text size="small" @click="download(snapshot)">下载</el-button></td></tr><tr v-if="!history.length"><td colspan="6" class="muted">{{ historyLoading ? '正在读取配置备份…' : '暂无' + kindLabel(configurationKind) + '记录' }}</td></tr></tbody></table>
      </div>
    </section>
    <section v-if="history.length > 1" class="card">
      <div class="card__header"><h2>{{ kindLabel(configurationKind) }}版本差异</h2><el-button size="small" :disabled="!canCompare" :loading="comparing" @click="compare">比较</el-button></div>
      <div class="card__body">
        <div class="form-grid"><label class="form-field"><span>基线</span><select v-model="baseId" aria-label="比较基线"><option v-for="item in history" :key="item.id" :value="item.id">{{ kindLabel(kindOf(item)) }} v{{ item.version }}</option></select></label><label class="form-field"><span>目标</span><select v-model="targetId" aria-label="比较目标"><option v-for="item in history" :key="item.id" :value="item.id">{{ kindLabel(kindOf(item)) }} v{{ item.version }}</option></select></label></div>
        <div v-if="diff" class="diff-grid" style="margin-top:14px"><article class="card diff-panel"><h3>新增 {{ diff.addedLines }} 行</h3><small v-if="diff.addedLines > diff.added.length" class="muted">已显示前{{ diff.added.length }}行，以上为差异总行数</small><pre v-for="(line, index) in diff.added" :key="'a-' + index">+ {{ line }}</pre></article><article class="card diff-panel"><h3>删除 {{ diff.removedLines }} 行</h3><small v-if="diff.removedLines > diff.removed.length" class="muted">已显示前{{ diff.removed.length }}行，以上为差异总行数</small><pre v-for="(line, index) in diff.removed" :key="'r-' + index">- {{ line }}</pre></article></div>
      </div>
    </section>
  </NcLayout>
</template>
