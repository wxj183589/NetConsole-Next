<script setup lang="ts">
import { computed, onActivated, onDeactivated, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import SyslogWorkspace from '../components/SyslogWorkspace.vue'
import { ApiRequestError, getHostTools, getTerminalDevices, getDeviceInventory, updateHostTool, launchHostTool, startNetworkDiagnostics, listNetworkDiagnostics, getNetworkDiagnostics, cancelNetworkDiagnostics, type HostTool, type TerminalDevice, type DeviceInventoryItem, type NetworkDiagnosticKind, type NetworkDiagnosticRun, type NetworkDiagnosticRunSummary } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const tools = ref<HostTool[]>([])
const devices = ref<TerminalDevice[]>([])
const selectedDevice = ref('')
const loading = ref(false)
const saving = ref('')
const editing = ref<Record<string, string>>({})
const inventory = ref<DeviceInventoryItem[]>([])
const selectedDiagnosticDevices = ref<string[]>([])
const diagnosticKinds = ref<NetworkDiagnosticKind[]>(['PING', 'TCP', 'DNS'])
const diagnosticRun = ref<NetworkDiagnosticRun | null>(null)
const diagnosticHistory = ref<NetworkDiagnosticRunSummary[]>([])
const selectedDiagnosticRun = ref('')
const diagnosticHistoryLoading = ref(false)
const diagnosticHistoryError = ref('')
const diagnosticKindOptions: NetworkDiagnosticKind[] = ['PING', 'QUICK_PING', 'TCP', 'DNS', 'TRACEROUTE', 'LLDP']
const diagnosticTimeoutMs = ref(2000)
const quickPingSamples = ref(10)
const quickPingInterval = ref(250)
const quickPingProtocol = ref<'ICMP' | 'TCP'>('ICMP')
const diagnosticStarting = ref(false)
const diagnosticCancelling = ref(false)
const diagnosticReading = ref(false)
const diagnosticError = ref('')
const diagnosticActive = computed(() => diagnosticRun.value?.state === 'QUEUED' || diagnosticRun.value?.state === 'RUNNING')
let diagnosticPoll: ReturnType<typeof setTimeout> | undefined
let diagnosticGeneration = 0
let diagnosticReadGeneration = -1
let diagnosticHistoryGeneration = 0
let pageActive = true
const isSuperAdmin = () => auth.user?.role === 'SUPERADMIN'

async function refresh(): Promise<void> {
  loading.value = true
  try {
    tools.value = await getHostTools()
    tools.value.forEach(tool => { if (editing.value[tool.id] === undefined) editing.value[tool.id] = tool.executablePath || '' })
    try { devices.value = await getTerminalDevices() } catch { devices.value = [] }
    try { inventory.value = (await getDeviceInventory({ pageSize: 100 })).items.filter(item => item.enabled && item.isValid) } catch { inventory.value = [] }
    if (!selectedDevice.value) selectedDevice.value = devices.value[0]?.id || ''
  }
  catch { ElMessage.error('宿主机工具或设备列表读取失败') }
  finally { loading.value = false }
}
function stopDiagnosticPolling(): void { if (diagnosticPoll) { clearTimeout(diagnosticPoll); diagnosticPoll = undefined } }
function diagnosticCurrent(id: string, generation: number): boolean { return pageActive && diagnosticGeneration === generation && selectedDiagnosticRun.value === id }
function clearDiagnosticSelection(id = ''): void {
  diagnosticGeneration++
  stopDiagnosticPolling()
  selectedDiagnosticRun.value = id
  diagnosticRun.value = null
  diagnosticError.value = ''
  diagnosticStarting.value = false
  diagnosticCancelling.value = false
  diagnosticReading.value = false
}
function rememberDiagnostic(run: NetworkDiagnosticRun): void {
  const summary: NetworkDiagnosticRunSummary = { id: run.id, state: run.state, totalDevices: run.totalDevices,
    totalChecks: run.totalChecks, completedChecks: run.completedChecks, progressPercent: run.progressPercent,
    createdMs: run.createdMs, startedMs: run.startedMs, endedMs: run.endedMs,
    kinds: [...new Set(run.items.map(item => item.kind))] }
  diagnosticHistory.value = [summary, ...diagnosticHistory.value.filter(item => item.id !== run.id)]
    .sort((left, right) => right.createdMs - left.createdMs || left.id.localeCompare(right.id)).slice(0, 50)
}
async function refreshDiagnosticHistory(): Promise<void> {
  const generation = ++diagnosticHistoryGeneration
  const selectionGeneration = diagnosticGeneration
  diagnosticHistoryLoading.value = true
  diagnosticHistoryError.value = ''
  try {
    const history = await listNetworkDiagnostics(50)
    if (!pageActive || generation !== diagnosticHistoryGeneration || selectionGeneration !== diagnosticGeneration) return
    diagnosticHistory.value = history
    if (selectedDiagnosticRun.value && !history.some(item => item.id === selectedDiagnosticRun.value)) clearDiagnosticSelection()
  } catch (cause) {
    if (!pageActive || generation !== diagnosticHistoryGeneration || selectionGeneration !== diagnosticGeneration) return
    if (cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status)) {
      diagnosticHistory.value = []
      clearDiagnosticSelection()
    }
    diagnosticHistoryError.value = '诊断历史读取失败，请刷新重试。'
  } finally { if (generation === diagnosticHistoryGeneration) diagnosticHistoryLoading.value = false }
}
function openDiagnosticHistory(): void {
  const id = selectedDiagnosticRun.value
  clearDiagnosticSelection(id)
  if (id) void pollDiagnostics(id)
}
async function pollDiagnostics(id: string, generation = diagnosticGeneration): Promise<void> {
  if (!diagnosticCurrent(id, generation) || diagnosticReadGeneration === generation) return
  stopDiagnosticPolling()
  diagnosticReadGeneration = generation
  diagnosticReading.value = true
  try {
    const run = await getNetworkDiagnostics(id)
    if (!diagnosticCurrent(id, generation)) return
    diagnosticRun.value = run
    rememberDiagnostic(run)
    diagnosticError.value = ''
    if (run.state === 'QUEUED' || run.state === 'RUNNING') diagnosticPoll = setTimeout(() => { void pollDiagnostics(id, generation) }, 700)
  } catch (cause) {
    if (diagnosticCurrent(id, generation)) {
      if (cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status)) {
        diagnosticHistoryGeneration++
        diagnosticHistoryLoading.value = false
        diagnosticHistory.value = cause.status === 404 ? diagnosticHistory.value.filter(item => item.id !== id) : []
        clearDiagnosticSelection()
        diagnosticError.value = '诊断记录已不存在或不可访问，请刷新历史。'
      } else diagnosticError.value = cause instanceof Error ? cause.message : '诊断结果读取失败'
    }
  } finally {
    if (diagnosticReadGeneration === generation) { diagnosticReadGeneration = -1; diagnosticReading.value = false }
  }
}
function selectDiagnosticKind(): void {
  if (diagnosticKinds.value.includes('QUICK_PING') && diagnosticTimeoutMs.value > 5000) diagnosticTimeoutMs.value = 2000
  if (!diagnosticKinds.value.includes('QUICK_PING') && diagnosticKinds.value.includes('TRACEROUTE') && diagnosticTimeoutMs.value < 10000) diagnosticTimeoutMs.value = 30000
}
async function startDiagnostics(): Promise<void> {
  if (diagnosticStarting.value || diagnosticActive.value) return
  if (!isSuperAdmin()) { ElMessage.warning('仅 SUPERADMIN 可启动诊断任务'); return }
  if (!selectedDiagnosticDevices.value.length) { ElMessage.warning('请选择至少一台设备'); return }
  if (!diagnosticKinds.value.length) { ElMessage.warning('请选择至少一种诊断类型'); return }
  if (diagnosticKinds.value.includes('QUICK_PING') &&
    (!Number.isInteger(quickPingSamples.value) || quickPingSamples.value < 2 || quickPingSamples.value > 60 ||
     !Number.isInteger(quickPingInterval.value) || quickPingInterval.value < 100 || quickPingInterval.value > 5000 ||
     diagnosticTimeoutMs.value > 5000 ||
     quickPingSamples.value * diagnosticTimeoutMs.value + (quickPingSamples.value - 1) * quickPingInterval.value > 120000)) {
    ElMessage.warning('Quick Ping 需 2–60 次采样、100–5000 ms 间隔和不超过 5000 ms 的单次超时，总预算不超过 120 秒'); return
  }
  stopDiagnosticPolling()
  const generation = ++diagnosticGeneration
  diagnosticStarting.value = true
  diagnosticError.value = ''
  try {
    const run = await startNetworkDiagnostics({ deviceIds: [...selectedDiagnosticDevices.value], kinds: [...diagnosticKinds.value], timeoutMs: diagnosticTimeoutMs.value, ...(diagnosticKinds.value.includes('QUICK_PING') ? { sampleCount: quickPingSamples.value, intervalMs: quickPingInterval.value, quickPingProtocol: quickPingProtocol.value } : {}) })
    if (!pageActive || generation !== diagnosticGeneration) return
    selectedDiagnosticRun.value = run.id
    diagnosticRun.value = run
    rememberDiagnostic(run)
    if (run.state === 'QUEUED' || run.state === 'RUNNING') void pollDiagnostics(run.id, generation)
  } catch (cause) {
    if (pageActive && generation === diagnosticGeneration) diagnosticError.value = cause instanceof Error ? cause.message : '诊断任务启动失败'
  } finally { if (generation === diagnosticGeneration) diagnosticStarting.value = false }
}
async function cancelDiagnostics(): Promise<void> {
  if (!diagnosticRun.value || !diagnosticActive.value || diagnosticCancelling.value || !isSuperAdmin()) return
  const id = diagnosticRun.value.id
  const generation = ++diagnosticGeneration
  stopDiagnosticPolling()
  diagnosticCancelling.value = true
  diagnosticError.value = ''
  try { await cancelNetworkDiagnostics(id); if (diagnosticCurrent(id, generation)) await pollDiagnostics(id, generation) }
  catch (cause) { if (diagnosticCurrent(id, generation)) diagnosticError.value = cause instanceof Error ? cause.message : '诊断任务取消失败' }
  finally { if (generation === diagnosticGeneration) diagnosticCancelling.value = false }
}
function suspendDiagnostics(): void { pageActive = false; diagnosticGeneration++; diagnosticHistoryGeneration++; diagnosticHistoryLoading.value = false; diagnosticStarting.value = false; diagnosticCancelling.value = false; diagnosticReading.value = false; stopDiagnosticPolling() }
function diagnosticHistoryLabel(item: NetworkDiagnosticRunSummary): string { return `${new Date(item.createdMs).toLocaleString('zh-CN', { hour12: false })} · ${diagnosticStateLabel(item.state)} · ${item.totalDevices} 台设备 · ${item.kinds.map(diagnosticKindLabel).join('、')}` }
function diagnosticKindLabel(value: string): string { return ({ QUICK_PING: 'Quick Ping 连续采样', PING: 'Ping', TCP: 'TCP 端口', DNS: 'DNS', TRACEROUTE: 'Traceroute 路径', LLDP: 'LLDP 事实' } as Record<string, string>)[value] || value }
function diagnosticStateLabel(value: string): string { return ({ QUEUED: '排队中', RUNNING: '执行中', SUCCEEDED: '已完成', PARTIAL: '部分完成', FAILED: '失败', CANCELLED: '已取消', NO_EVIDENCE: '暂无证据' } as Record<string, string>)[value] || value }
function diagnosticOutcomeLabel(value?: string | null): string { return ({ OK: '检查成功', TRACEROUTE_MAX_HOPS: '达到跳数上限，尚未到达目标', TRACEROUTE_UNREACHABLE: '目标不可达', TIMEOUT: '检查总超时', CANCELLED: '已取消', DIAGNOSTIC_SCOPE_REVOKED: '设备访问权限已变化' } as Record<string, string>)[value || ''] || value || '—' }
function hopStatusLabel(value: string): string { return ({ REACHED: '已到达目标', TTL_EXPIRED: '中间节点', TIMED_OUT: '该跳超时', UNREACHABLE: '不可达', OTHER: '其他响应' } as Record<string, string>)[value] || '未知响应' }
async function save(tool: HostTool): Promise<void> {
  saving.value = tool.id
  try { const updated = await updateHostTool(tool.id, editing.value[tool.id] || null); tools.value = tools.value.map(item => item.id === tool.id ? updated : item); ElMessage.success(tool.displayName + ' 配置已保存') }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '保存失败') }
  finally { saving.value = '' }
}
async function launch(tool: HostTool): Promise<void> {
  if (!selectedDevice.value) { ElMessage.warning('请先选择已登记设备'); return }
  try {
    const result = await launchHostTool(tool.id, selectedDevice.value)
    if (result.status === 'LAUNCHED') ElMessage.success(tool.displayName + ' 已请求宿主机桌面启动')
    else ElMessage.warning(result.detail || result.status)
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '启动请求失败') }
}
function statusLabel(value: string): string { return ({ READY: '可用', NOT_CONFIGURED: '未配置', EXECUTABLE_NOT_FOUND: '程序不存在', HOST_WINDOWS_REQUIRED: '需要 Windows 宿主机', REPARSE_POINT_BLOCKED: '路径被阻断', EXECUTABLE_NOT_ALLOWED: '不是允许的程序' } as Record<string, string>)[value] || value }
onMounted(() => { void refresh(); void refreshDiagnosticHistory() })
onUnmounted(suspendDiagnostics)
onDeactivated(suspendDiagnostics)
onActivated(() => { if (!pageActive) { pageActive = true; void refreshDiagnosticHistory(); if (selectedDiagnosticRun.value && diagnosticActive.value) void pollDiagnostics(selectedDiagnosticRun.value) } })
watch(() => [auth.user?.username, auth.user?.role], () => { clearDiagnosticSelection(); diagnosticHistory.value = []; diagnosticHistoryError.value = ''; diagnosticHistoryGeneration++; if (pageActive) void refreshDiagnosticHistory() })
</script>

<template>
  <NcLayout title="工具集" description="宿主机外部终端注册与受控启动；程序运行在安装 NetConsole Next 的 Windows 宿主机桌面">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <SyslogWorkspace :identity-key="`${auth.user?.username ?? ''}:${auth.user?.role ?? ''}`" />
    <div class="card card--flat" style="margin-bottom:14px"><div class="card__body" style="line-height:1.8;color:var(--nc-text-secondary)">外部 SecureCRT、Xshell、PuTTY 不会在访问网页的笔记本上启动。服务只通过本机 Named Pipe 请求 Desktop Helper；密码、凭据引用和任意 shell 命令都不会进入启动参数。</div></div>
    <section class="card" style="margin-bottom:14px">
      <div class="card__header"><div><h2>网络诊断</h2><small>从 NetConsole 服务所在主机检查已登记设备；LLDP 读取当前局点内已验证的事实</small></div><span v-if="diagnosticRun" class="state-pill" :class="diagnosticRun.state === 'SUCCEEDED' ? 'good' : diagnosticRun.state === 'PARTIAL' ? 'warn' : ''">{{ diagnosticStateLabel(diagnosticRun.state) }} · {{ diagnosticRun.progressPercent }}%</span></div>
      <div class="card__body">
        <div class="toolbar diagnostic-history">
          <label class="form-field"><span>诊断历史（最近 50 条）</span><select v-model="selectedDiagnosticRun" aria-label="诊断历史" :disabled="diagnosticStarting" @change="openDiagnosticHistory"><option value="">选择已有任务查看结果</option><option v-for="record in diagnosticHistory" :key="record.id" :value="record.id">{{ diagnosticHistoryLabel(record) }}</option></select></label>
          <el-button size="small" :loading="diagnosticHistoryLoading" :disabled="diagnosticHistoryLoading" @click="refreshDiagnosticHistory">刷新历史</el-button>
          <small class="muted">查看历史只读取已保存结果。</small>
        </div>
        <p v-if="diagnosticHistoryError" role="alert" class="diagnostic-error">{{ diagnosticHistoryError }}</p>
        <div class="toolbar diagnostic-controls">
          <label class="form-field diagnostic-devices"><span>设备（可多选）</span><select v-model="selectedDiagnosticDevices" aria-label="诊断设备" multiple size="4" :disabled="diagnosticStarting || diagnosticActive"><option v-for="device in inventory" :key="device.id" :value="device.id">{{ device.name }} · {{ device.managementIp }}</option></select></label>
          <div class="form-field"><span>检查类型</span><label v-for="kind in diagnosticKindOptions" :key="kind" class="check-row"><input v-model="diagnosticKinds" type="checkbox" :value="kind" :disabled="diagnosticStarting || diagnosticActive" @change="selectDiagnosticKind" />{{ diagnosticKindLabel(kind) }}</label></div>
          <label class="form-field"><span>每项检查总超时</span><select v-model.number="diagnosticTimeoutMs" aria-label="每项检查总超时" :disabled="diagnosticStarting || diagnosticActive"><option :value="2000">2 秒</option><option :value="5000">5 秒</option><option :value="10000">10 秒</option><option :value="30000">30 秒</option></select><small class="muted">Traceroute 最多 30 跳，建议 30 秒。</small></label>
          <fieldset v-if="diagnosticKinds.includes('QUICK_PING')" class="quick-ping-options" :disabled="diagnosticStarting || diagnosticActive"><legend>Quick Ping</legend>
            <label class="form-field"><span>探测方式</span><select v-model="quickPingProtocol" aria-label="Quick Ping 协议"><option>ICMP</option><option>TCP</option></select></label>
            <label class="form-field"><span>采样次数</span><input v-model.number="quickPingSamples" aria-label="Quick Ping 采样次数" type="number" min="2" max="60" /></label>
            <label class="form-field"><span>采样间隔 (ms)</span><input v-model.number="quickPingInterval" aria-label="Quick Ping 采样间隔" type="number" min="100" max="5000" step="50" /></label>
            <small class="muted">最多 8 台设备，单次超时 ≤5 秒，每台总预算 ≤120 秒；TCP 使用设备登记端口。</small>
          </fieldset>
          <div class="toolbar" style="align-self:flex-end"><el-button type="primary" size="small" :loading="diagnosticStarting" :disabled="!isSuperAdmin() || diagnosticStarting || diagnosticActive" @click="startDiagnostics">开始诊断</el-button><el-button size="small" :loading="diagnosticCancelling" :disabled="!diagnosticActive || diagnosticCancelling || !isSuperAdmin()" @click="cancelDiagnostics">取消任务</el-button></div>
        </div>
        <div v-if="diagnosticError" role="alert" class="diagnostic-error"><span>{{ diagnosticError }}</span><el-button v-if="selectedDiagnosticRun && !diagnosticCancelling" size="small" :loading="diagnosticReading" :disabled="diagnosticReading" @click="pollDiagnostics(selectedDiagnosticRun)">{{ diagnosticActive ? '重新读取进度' : '重新读取结果' }}</el-button></div>
        <template v-if="diagnosticRun">
          <div class="diagnostic-progress" role="status" aria-live="polite"><span>已完成 {{ diagnosticRun.completedChecks }} / {{ diagnosticRun.totalChecks }} 项检查 · {{ diagnosticRun.totalDevices }} 台设备</span><progress :value="diagnosticRun.progressPercent" max="100" aria-label="网络诊断进度" /><span>{{ diagnosticRun.progressPercent }}%</span></div>
          <p v-if="diagnosticRun.failureReason" class="muted">{{ diagnosticRun.failureReason }}</p>
          <div class="table-wrap diagnostic-results"><table class="data-table"><thead><tr><th>设备</th><th>类型</th><th>状态</th><th>结果</th><th>总耗时</th><th>证据</th></tr></thead><tbody><template v-for="item in diagnosticRun.items" :key="item.id">
            <tr><td>{{ item.deviceName }}</td><td>{{ diagnosticKindLabel(item.kind) }}</td><td>{{ diagnosticStateLabel(item.state) }}</td><td>{{ item.detail || diagnosticOutcomeLabel(item.outcome) }}</td><td>{{ item.latencyMs == null ? '—' : item.latencyMs + ' ms' }}</td><td>{{ item.evidenceCount ? item.evidenceCount + ' 条 · ' + item.evidenceSources.join(', ') : '—' }}</td></tr>
            <tr v-if="item.kind === 'QUICK_PING'" class="trace-row"><td colspan="6"><section class="trace-route" :aria-label="item.deviceName + ' Quick Ping 结果'">
              <template v-if="item.quickPing"><div class="trace-heading"><strong>{{ item.quickPing.protocol }} · {{ item.quickPing.destinationAddress }}{{ item.quickPing.port ? ':' + item.quickPing.port : '' }}</strong><span>采样 {{ item.quickPing.sent }}/{{ item.quickPing.requestedSamples }}</span><span :class="['state-pill', item.quickPing.lost ? 'warn' : 'good']">丢失率 {{ item.quickPing.lossPercent ?? 'UNKNOWN' }}%</span><span>最小 / 平均 / 最大 {{ item.quickPing.minMs ?? '—' }} / {{ item.quickPing.averageMs ?? '—' }} / {{ item.quickPing.maxMs ?? '—' }} ms</span></div>
              <div class="quick-ping-samples" aria-label="采样时间线"><span v-for="sample in item.quickPing.samples" :key="sample.sequence" :class="['quick-ping-sample', sample.status === 'REPLIED' ? 'good' : 'lost']" :title="'#' + sample.sequence + ' · ' + new Date(sample.capturedMs).toLocaleTimeString() + ' · ' + sample.status">{{ sample.latencyMs == null ? '×' : sample.latencyMs + 'ms' }}</span></div>
              <small class="muted">丢失率按已完成采样计算；取消后未执行的采样不计为丢失。TCP 结果代表端口连通性。</small></template><p v-else class="muted">等待采样结果。</p>
            </section></td></tr>
            <tr v-if="item.kind === 'TRACEROUTE'" class="trace-row"><td colspan="6"><section :aria-label="item.deviceName + ' Traceroute 路径'" class="trace-route">
              <div class="trace-heading"><strong>逐跳路径</strong><span v-if="item.traceRoute" class="mono">目标 {{ item.traceRoute.destinationAddress }}</span><span :class="['state-pill', item.traceRoute?.destinationReached ? 'good' : 'warn']">{{ item.traceRoute?.destinationReached ? '已到达目标' : item.state === 'QUEUED' ? '等待探测' : item.state === 'RUNNING' ? '正在探测路径' : '未到达目标' }}</span><span v-if="item.traceRoute" class="muted">已记录 {{ item.traceRoute.hops.length }} / {{ item.traceRoute.maxHops }} 跳</span></div>
              <table v-if="item.traceRoute?.hops.length" class="trace-table"><thead><tr><th>跳数</th><th>响应节点</th><th>状态</th><th>响应耗时</th></tr></thead><tbody><tr v-for="hop in item.traceRoute.hops" :key="hop.ttl"><td>{{ hop.ttl }}</td><td class="mono">{{ hop.address || '—' }}</td><td><span :class="hop.status === 'REACHED' ? 'trace-hop--good' : hop.status === 'TIMED_OUT' || hop.status === 'UNREACHABLE' ? 'trace-hop--warn' : ''">{{ hopStatusLabel(hop.status) }}</span></td><td>{{ hop.latencyMs == null ? '—' : hop.latencyMs + ' ms' }}</td></tr></tbody></table>
              <p v-else class="muted">{{ item.state === 'RUNNING' || item.state === 'QUEUED' ? '等待逐跳响应，结果会自动更新。' : '暂无逐跳响应。' }}</p>
              <small class="muted">超时节点可能不响应探测；未到达目标时仅展示已取得的部分路径。</small>
            </section></td></tr>
          </template></tbody></table></div>
        </template>
        <div v-else class="empty-state" style="margin-top:12px"><div><strong>{{ diagnosticReading ? '正在读取诊断结果' : '尚未运行诊断' }}</strong><small>选择历史查看已有结果，或选择设备和检查类型开始批量任务。</small></div></div>
      </div>
    </section>
    <div class="toolbar" style="margin-bottom:12px"><label class="form-field" style="min-width:300px"><span>外部终端目标设备</span><select v-model="selectedDevice"><option value="">选择已登记设备</option><option v-for="device in devices" :key="device.id" :value="device.id">{{ device.host }}:{{ device.port }} · {{ device.username }}</option></select></label><span v-if="!isSuperAdmin()" class="state-pill warn">仅 SUPERADMIN 可启动和配置</span></div>
    <div v-loading="loading" class="tool-grid">
      <article v-for="tool in tools" :key="tool.id" class="card tool-card">
        <div class="card__header"><div><h2>{{ tool.displayName }}</h2><small>{{ tool.product }} · 宿主机程序</small></div><span :class="['state-pill', tool.status === 'READY' ? 'good' : 'warn']">{{ statusLabel(tool.status) }}</span></div>
        <label class="form-field"><span>宿主机可执行文件绝对路径</span><input v-model="editing[tool.id]" :placeholder="tool.executablePath || '例如 C:\\Program Files\\...\\程序.exe'" :disabled="!isSuperAdmin()" autocomplete="off" /></label>
        <small class="muted">{{ tool.detail || '路径已通过安全检查；启动前仍由 Desktop Helper 再次校验。' }}</small>
        <div class="toolbar" style="margin-top:14px"><el-button size="small" :disabled="!isSuperAdmin() || tool.status !== 'READY' || !selectedDevice" @click="launch(tool)">启动到宿主机桌面</el-button><el-button v-if="isSuperAdmin()" size="small" type="primary" :loading="saving === tool.id" @click="save(tool)">保存路径</el-button></div>
      </article>
      <div v-if="!tools.length" class="card"><div class="empty-state"><div><strong>暂无工具注册信息</strong><small>服务没有创建虚假的安装状态。</small></div></div></div>
    </div>
  </NcLayout>
</template>

<style scoped>
.quick-ping-options { display: flex; flex-wrap: wrap; gap: 12px; border: 1px solid var(--nc-border); max-width: 540px; }
.quick-ping-options .form-field { width: 135px; }
.quick-ping-samples { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 12px; }
.quick-ping-sample { padding: 4px 7px; border-radius: 3px; background: var(--nc-surface); border: 1px solid var(--nc-success); font-size: 12px; }
.quick-ping-sample.lost { border-color: var(--nc-danger); color: var(--nc-danger); }
.diagnostic-controls { align-items: flex-start; gap: 18px; flex-wrap: wrap; }
.diagnostic-history { gap: 12px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 18px; }
.diagnostic-history .form-field { flex: 1 1 440px; min-width: 0; max-width: 760px; }
.diagnostic-history select { width: 100%; }
.diagnostic-devices { flex: 1 1 360px; max-width: 560px; min-width: 0; }
.diagnostic-devices select { width: 100%; }
.diagnostic-controls .check-row { display: flex; align-items: center; gap: 7px; white-space: nowrap; }
.diagnostic-controls .check-row input { width: auto; margin: 0; }
.diagnostic-error { display: flex; align-items: center; gap: 12px; margin-top: 14px; color: var(--nc-danger, #c93442); }
.diagnostic-progress { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-top: 16px; color: var(--nc-text-secondary); }
.diagnostic-progress progress { width: 200px; height: 10px; accent-color: var(--nc-primary, #1677ff); }
.diagnostic-results { margin-top: 12px; }
.diagnostic-results th, .diagnostic-results td { white-space: normal; }
.trace-row > td { padding-top: 0; }
.trace-route { padding: 14px 18px; background: var(--nc-surface-muted); border: 1px solid var(--nc-border); border-radius: 6px; }
.trace-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 10px; }
.trace-heading .mono, .trace-table .mono { overflow-wrap: anywhere; }
.trace-table { width: 100%; border-collapse: collapse; table-layout: fixed; margin-bottom: 10px; }
.trace-table th, .trace-table td { padding: 7px 12px; text-align: left; border-bottom: 1px solid var(--nc-border, #dce5f1); }
.trace-table th { color: var(--nc-text-secondary); font-weight: 500; }
.trace-table th:first-child { width: 64px; }
.trace-hop--good { color: var(--nc-success); }
.trace-hop--warn { color: var(--nc-warning); }
</style>
