<script setup lang="ts">
import { onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { ApiRequestError, cancelTask, createH3cTaskDefinition, downloadTaskDiagnostics, downloadTaskHistory, getH3cProfiles, getTaskCommands, getTaskDiagnostics, getTasks, resumeTask, retryTask, startH3cTaskDefinition, type TaskCommand, type TaskDiagnosticManifest, type TaskRun, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { canCancelTask, canResumeTask, canRetryTask, captureStatusLabel, parseStatusLabel, taskFailureLabel, taskStateLabel, taskTypeLabel } from '../taskViewModel'

const auth = useAuthStore()
const route = useRoute()
const tasks = ref<TaskRun[]>([]); const loading = ref(false); const actionTaskId = ref<string | null>(null); const commandLoading = ref(false); const showCommandDialog = ref(false); const commandTask = ref<TaskRun | null>(null); const commands = ref<TaskCommand[]>([])
const diagnostics = ref<TaskDiagnosticManifest | null>(null); const diagnosticError = ref(''); const downloading = ref<string | null>(null)
let commandGeneration = 0; let commandRequest: AbortController | null = null; let downloadRequest: AbortController | null = null
const profiles = ref<TerminalDevice[]>([]); const selectedProfileId = ref(''); const selectedCommands = ref<string[]>(['display version']); const planTimeoutMs = ref(30000); const planLoading = ref(false)
const historyDownloading = ref(false)
const planCommands = [
  ['display version', '设备版本'], ['display interface brief', '接口摘要'], ['display device', '设备状态'],
  ['display lldp neighbor-information list', 'LLDP 邻居'], ['display wlan ap all', 'FIT-AP 列表'],
  ['display wlan ap all address', 'FIT-AP 地址'], ['display wlan ap all radio', 'FIT-AP 射频'],
  ['display vlan all', 'VLAN 信息'], ['display wlan ap all radio verbose filter bbssid', 'AP 射频 BSSID'],
  ['display wlan ap all connection-record', 'AP 连接记录'], ['display wlan ap all radio type', 'AP 射频类型'],
  ['display wlan ap unauthenticated', '未认证 AP'], ['display wlan ap all lldp', 'AP LLDP'],
  ['display wlan ap all verbose', 'AP 详细回显'], ['display cpu-usage', 'CPU 使用率'],
  ['display memory', '内存使用'], ['display device manuinfo', '设备制造信息'],
  ['display wlan ac-role', '控制器角色'],
] as const
function selectAcPlan(): void {
  selectedCommands.value = ['display wlan ap all', 'display wlan ap all address', 'display wlan ap all radio']
  planTimeoutMs.value = 60000
}
async function refresh(): Promise<void> { closeCommands(); loading.value = true; try { tasks.value = await getTasks() } catch { ElMessage.error('任务 API 读取失败') } finally { loading.value = false } }
async function refreshProfiles(): Promise<void> { try { profiles.value = await getH3cProfiles(); if (!selectedProfileId.value) selectedProfileId.value = profiles.value[0]?.id || '' } catch { profiles.value = [] } }
async function downloadHistory(): Promise<void> {
  if (historyDownloading.value) return
  historyDownloading.value = true
  try {
    const blob = await downloadTaskHistory(); const url = URL.createObjectURL(blob); const link = document.createElement('a')
    try { link.href = url; link.download = `tasks-${new Date().toISOString().slice(0, 19).replaceAll(/[-:T]/g, '')}.csv`; link.hidden = true; document.body.appendChild(link); link.click() }
    finally { link.remove(); URL.revokeObjectURL(url) }
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '任务历史导出失败') }
  finally { historyDownloading.value = false }
}
function time(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string { return ['COMPLETED', 'SUCCEEDED', 'RUNNING'].includes(state) ? 'good' : ['FAILED', 'INTERRUPTED'].includes(state) ? 'bad' : 'warn' }
async function showCommands(task: TaskRun): Promise<void> {
  clearCommands()
  commandTask.value = task; showCommandDialog.value = true; commandLoading.value = true
  const generation = commandGeneration; const controller = new AbortController(); commandRequest = controller
  const [detail, manifest] = await Promise.allSettled([getTaskCommands(task.id, controller.signal), getTaskDiagnostics(task.id, controller.signal)])
  if (!currentCommands(generation, task.id)) return
  const denied = detail.status === 'rejected' && scopeDenied(detail.reason) ? detail.reason : manifest.status === 'rejected' && scopeDenied(manifest.reason) ? manifest.reason : null
  if (denied) { diagnosticError.value = diagnosticFailure(denied, '命令明细读取失败'); commandLoading.value = false; return }
  if (detail.status === 'fulfilled') commands.value = detail.value
  else ElMessage.error('命令明细读取失败')
  if (manifest.status === 'fulfilled' && manifest.value.runId === task.id) diagnostics.value = manifest.value
  else {
    const cause = manifest.status === 'rejected' ? manifest.reason : null
    diagnosticError.value = diagnosticFailure(cause, '诊断输出读取失败，请重新打开明细。')
    if (scopeDenied(cause)) commands.value = []
  }
  commandLoading.value = false
}
function clearCommands(): void {
  ++commandGeneration; commandRequest?.abort(); downloadRequest?.abort(); commandRequest = null; downloadRequest = null
  commands.value = []; diagnostics.value = null; diagnosticError.value = ''; downloading.value = null; commandLoading.value = false; commandTask.value = null
}
function closeCommands(): void { showCommandDialog.value = false; clearCommands() }
function currentCommands(generation: number, runId: string): boolean { return generation === commandGeneration && showCommandDialog.value && commandTask.value?.id === runId }
function scopeDenied(cause: unknown): boolean { return cause instanceof ApiRequestError && ([401, 403, 404].includes(cause.status) || cause.message.includes('DIAGNOSTIC_SCOPE_REVOKED')) }
function diagnosticFailure(cause: unknown, fallback: string): string {
  if (scopeDenied(cause)) return '任务不存在或访问权限已变化，请刷新任务列表。'
  if (cause instanceof ApiRequestError) {
    if (cause.status === 413) return '诊断输出过大，请按单条命令下载。'
    if (cause.message.includes('DIAGNOSTIC_RAW_INVALID')) return '诊断输出校验失败，暂不可下载。'
    if (cause.message.includes('DIAGNOSTIC_TASK_ACTIVE')) return '任务仍在采集中，请稍后重新打开明细。'
    if (cause.status === 409) return '该命令暂无可下载的诊断输出。'
  }
  return fallback
}
function canDownloadDiagnostic(index?: number, manifest = diagnostics.value): boolean {
  if (!manifest || manifest.runId !== commandTask.value?.id) return false
  if (index === undefined) return manifest.canDownload
  const item = manifest.commands.find(item => item.index === index)
  return Boolean(item?.canDownload && commands.value.some(command => command.index === index && command.command === item.command))
}
function diagnosticLabel(index: number): string {
  const item = diagnostics.value?.commands.find(item => item.index === index)
  const labels = { READY: '可下载', NO_RAW: '暂无输出', CONFIG_EXCLUDED: '配置内容已排除', UNSUPPORTED_COMMAND: '暂不支持下载', TASK_ACTIVE: '采集中', RAW_INVALID: '输出校验失败' }
  return item ? labels[item.availability] || '暂不可下载' : '暂不可下载'
}
async function downloadDiagnostic(index?: number): Promise<void> {
  const task = commandTask.value
  if (!task || !canDownloadDiagnostic(index) || downloading.value !== null || commandLoading.value) return
  const generation = commandGeneration; const controller = new AbortController(); downloadRequest = controller
  downloading.value = index === undefined ? 'all' : String(index); diagnosticError.value = ''
  try {
    const blob = await downloadTaskDiagnostics(task.id, index, controller.signal)
    if (!currentCommands(generation, task.id)) return
    const latest = await getTaskDiagnostics(task.id, controller.signal)
    if (!currentCommands(generation, task.id)) return
    diagnostics.value = latest.runId === task.id ? latest : null
    if (!canDownloadDiagnostic(index)) { diagnosticError.value = '诊断输出的可用性已变化，请重新打开明细。'; return }
    const url = URL.createObjectURL(blob); const link = document.createElement('a')
    try {
      link.href = url
      link.download = index === undefined ? `diagnostics-${task.id}.zip` : `diagnostics-${task.id}-command-${String(index + 1).padStart(3, '0')}.txt`
      link.hidden = true; document.body.appendChild(link); link.click()
    } finally { link.remove(); URL.revokeObjectURL(url) }
  } catch (cause) {
    if (currentCommands(generation, task.id)) {
      diagnostics.value = null
      if (scopeDenied(cause)) commands.value = []
      diagnosticError.value = diagnosticFailure(cause, '诊断输出下载失败，请重新打开明细后重试。')
    }
  } finally { if (currentCommands(generation, task.id)) { downloading.value = null; downloadRequest = null } }
}
async function runAction(task: TaskRun, action: 'cancel' | 'retry' | 'resume'): Promise<void> {
  actionTaskId.value = task.id
  try {
    if (action === 'cancel') await cancelTask(task.id)
    else if (action === 'retry') await retryTask(task.id)
    else await resumeTask(task.id)
    ElMessage.success(action === 'cancel' ? '取消请求已提交' : action === 'retry' ? '重试任务已提交' : '恢复任务已提交')
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '任务操作失败') } finally { actionTaskId.value = null }
}
async function createAndRunPlan(): Promise<void> {
  if (!auth.isSuperAdmin || !selectedProfileId.value || !selectedCommands.value.length) { ElMessage.warning('请选择设备和至少一条只读命令'); return }
  await submitPlan([...selectedCommands.value], planTimeoutMs.value)
}
async function createAndRunDiagnostic(): Promise<void> {
  if (!auth.isSuperAdmin || !selectedProfileId.value) { ElMessage.warning('请选择诊断设备'); return }
  await submitPlan(['display diagnostic-information'], 600000)
}
async function submitPlan(commandPlan: string[], timeoutMs: number): Promise<void> {
  if (planLoading.value) return
  planLoading.value = true
  try {
    const id = crypto.randomUUID().replaceAll('-', '')
    await createH3cTaskDefinition({ id, profileId: selectedProfileId.value, command: commandPlan[0], commands: commandPlan, timeoutMs, enabled: true })
    await startH3cTaskDefinition(id)
    ElMessage.success(`已提交 ${commandPlan.length} 条命令的采集计划`)
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '采集计划提交失败') } finally { planLoading.value = false }
}
onMounted(async () => { await Promise.all([refresh(), refreshProfiles()]) })
watch(showCommandDialog, visible => { if (!visible) clearCommands() }, { flush: 'sync' })
watch(() => route.fullPath, closeCommands, { flush: 'sync' })
watch(() => auth.user, closeCommands, { deep: true, flush: 'sync' })
onDeactivated(closeCommands)
onBeforeUnmount(closeCommands)
</script>
<template>
  <NcLayout title="任务中心" description="采集任务的服务端状态、Raw 输出与解析状态">
    <template #actions><el-button size="small" :loading="historyDownloading" @click="downloadHistory">导出任务 CSV</el-button><el-button size="small" @click="refresh">刷新</el-button></template>
    <section v-if="auth.isSuperAdmin" class="card"><div class="card__header"><div><h2>新建采集计划</h2><span class="muted">固定只读命令，按顺序执行并分别保存 Raw / Parse 状态</span></div><span class="state-pill">{{ selectedCommands.length }} 条命令</span></div><div class="card__body"><div class="form-grid"><label class="form-field"><span>目标设备</span><select v-model="selectedProfileId"><option value="">选择 H3C 设备</option><option v-for="profile in profiles" :key="profile.id" :value="profile.id">{{ profile.host }}:{{ profile.port }} · {{ profile.username }}</option></select></label><label class="form-field"><span>计划总超时</span><select v-model.number="planTimeoutMs"><option :value="10000">10 秒</option><option :value="30000">30 秒</option><option :value="60000">60 秒</option></select></label><div class="form-field form-field--full"><span>采集内容</span><div><el-button size="small" :disabled="planLoading" @click="selectAcPlan">选择 AC 基础采集</el-button><span class="muted"> 仅选择 AP 列表、地址和射频，确认后执行。</span></div><div class="overview-footer"><label v-for="item in planCommands" :key="item[0]" class="check-row"><input v-model="selectedCommands" type="checkbox" :value="item[0]" />{{ item[1] }}</label></div></div></div><div class="toolbar" style="margin-top:12px"><el-button type="primary" :loading="planLoading" :disabled="planLoading || !selectedProfileId || !selectedCommands.length" @click="createAndRunPlan">创建并执行计划</el-button><span class="muted">连接时自动使用设备已保存的用户名和密码，无需重复输入。</span></div><div class="toolbar" style="margin-top:12px"><el-button :disabled="planLoading || !selectedProfileId" :loading="planLoading" @click="createAndRunDiagnostic">采集全机诊断</el-button><span class="muted">只读显示诊断，最多 10 分钟；不会保存设备文件。完成后在命令明细下载 TXT / ZIP。</span></div></div></section>
    <div class="card"><div class="card__header"><h2>最近任务 <span class="state-pill">{{ tasks.length }} 条</span></h2><span class="state-pill">状态由服务端实时记录</span></div><div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th>操作</th><th>状态</th><th>时间</th><th>原始数据</th><th>识别结果</th><th>说明</th><th>任务操作</th><th>明细</th></tr></thead><tbody><tr v-for="task in tasks" :key="task.id"><td><span class="device-name">{{ taskTypeLabel(task.taskType) }}</span><span class="device-subline mono" :title="`内部任务编号：${task.id}`">内部编号 {{ task.id.slice(0, 8) }}</span></td><td><span :class="['state-pill', stateClass(task.state)]">{{ taskStateLabel(task.state) }}</span></td><td><span>{{ time(task.startedMs || task.queuedMs) }}</span><span class="device-subline">{{ task.endedMs ? `完成于 ${time(task.endedMs)}` : '仍在处理' }}</span></td><td>{{ captureStatusLabel(task.captureStatus) }}</td><td>{{ parseStatusLabel(task.parseStatus) }}</td><td class="muted">{{ taskFailureLabel(task.failureReason) }}</td><td><div class="table-actions"><el-button v-if="canCancelTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'cancel')">取消</el-button><el-button v-if="canRetryTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'retry')">重试</el-button><el-button v-if="canResumeTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'resume')">恢复</el-button><span v-if="!canCancelTask(task.state) && !canRetryTask(task.state) && !canResumeTask(task.state)" class="muted">—</span></div></td><td><el-button text size="small" @click="showCommands(task)">命令明细</el-button></td></tr><tr v-if="!tasks.length"><td colspan="8"><div class="empty-state"><div><strong>暂无任务数据</strong><small>任务中心只展示服务端真实创建的任务，不使用模拟数据填充。</small></div></div></td></tr></tbody></table></div></div>
    <el-dialog v-model="showCommandDialog" :title="commandTask ? `${taskTypeLabel(commandTask.taskType)} · 命令明细` : '命令明细'" width="min(1120px, calc(100vw - 32px))">
      <div class="toolbar" style="margin-bottom:12px"><span class="muted">下载采集时的诊断回显，密码等敏感字段会脱敏。</span><el-button size="small" :disabled="commandLoading || downloading !== null || !canDownloadDiagnostic()" :loading="downloading === 'all'" @click="downloadDiagnostic()">下载诊断 ZIP</el-button></div>
      <p v-if="diagnosticError" role="alert" class="bad-text">{{ diagnosticError }}</p>
      <p v-else-if="diagnostics?.status === 'PARTIAL'" class="muted">部分命令暂无可下载输出，详见各条状态。</p>
      <div v-loading="commandLoading" class="table-wrap"><table class="data-table"><thead><tr><th>#</th><th>命令</th><th>状态</th><th>Raw</th><th>解析</th><th>错误</th><th>诊断输出</th></tr></thead><tbody><tr v-for="command in commands" :key="command.index"><td>{{ command.index + 1 }}</td><td class="mono">{{ command.command }}</td><td><span :class="['state-pill', stateClass(command.state)]">{{ taskStateLabel(command.state) }}</span></td><td>{{ captureStatusLabel(command.captureStatus) }}</td><td>{{ parseStatusLabel(command.parseStatus) }}</td><td class="muted">{{ command.error || '—' }}</td><td><el-button v-if="canDownloadDiagnostic(command.index)" text size="small" :disabled="downloading !== null" :loading="downloading === String(command.index)" @click="downloadDiagnostic(command.index)">下载 TXT</el-button><span v-else class="muted">{{ commandLoading ? '读取中…' : diagnosticLabel(command.index) }}</span></td></tr><tr v-if="!commands.length && !commandLoading"><td colspan="7" class="muted">该任务暂无命令级记录。</td></tr></tbody></table></div>
    </el-dialog>
  </NcLayout>
</template>

