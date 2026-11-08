<script setup lang="ts">
import { onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { acknowledgeFault, processFault, getFaults, setFaultPolicy, syncFaults, verifyFault, type FaultFilters, type FaultIncident, type FaultResult } from '../api/faults'

const props = withDefaults(defineProps<{ canRecover?: boolean; identityKey?: string }>(), { canRecover: false, identityKey: '' })
const result = ref<FaultResult | null>(null)
const state = ref('')
const deviceId = ref('')
const kind = ref('')
const from = ref('')
const to = ref('')
const busy = ref(false)
const error = ref('')
const notice = ref('')
const expanded = ref('')
const interval = ref(5)
const attempts = ref(3)
const loadedScope = ref<{ filters: FaultFilters; at: string } | null>(null)
let generation = 0
let active = true
const time = (at: number | null) => at ? new Date(at).toLocaleString() : '—'
async function load(page = 1) {
  const current = ++generation
  busy.value = true; error.value = ''
  const filters = { state: state.value, deviceId: deviceId.value, kind: kind.value,
    fromMs: from.value ? new Date(from.value).getTime() : undefined, toMs: to.value ? new Date(to.value).getTime() : undefined }
  try {
    const data = await getFaults(page, filters)
    if (current === generation) { result.value = data; loadedScope.value = { filters, at: new Date().toISOString() } }
  } catch (cause) { if (current === generation) { result.value = null; error.value = cause instanceof Error ? cause.message : '故障历史读取失败' } }
  finally { if (current === generation) busy.value = false }
}
async function action(run: () => Promise<string>) {
  if (busy.value || !props.canRecover) return
  const current = generation
  busy.value = true; error.value = ''; notice.value = ''
  try { const message = await run(); if (current === generation) { notice.value = message; await load(result.value?.page ?? 1) } }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '故障操作失败' }
  finally { if (current === generation) busy.value = false }
}
const acknowledge = (item: FaultIncident) => action(async () => { await acknowledgeFault(item.id); return '已确认故障，仍等待恢复证据。' })
const process = (item: FaultIncident) => action(async () => { await processFault(item.id); return '已开始处理，仍等待同资源恢复证据。' })
const verify = (item: FaultIncident) => action(async () => { const report = await verifyFault(item.id); return `${report.state} · ${report.detail}` })
const synchronize = () => action(async () => { await syncFaults(); return '已同步第 1 页授权设备证据；其他页由后台轮巡。' })
const configure = (item: FaultIncident, enabled: boolean) => action(async () => {
  const report = await setFaultPolicy(item, enabled, interval.value, attempts.value)
  return `只读补采策略：${report.state}；连接和入队均不代表业务恢复。`
})
const policy = (item: FaultIncident) => result.value?.policies.find(p => p.deviceId === item.deviceId)
function exportCsv() {
  const data = result.value; const scope = loadedScope.value
  if (busy.value || error.value || !data?.items.length || !scope) return
  // Free text may contain device error output. Redact named credential patterns,
  // including multiline private keys; do not claim arbitrary business text is secret-free.
  const redact = (value: string) => value
    .replace(/-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)/g, '[REDACTED PRIVATE KEY]')
    .replace(/\b(Bearer|Basic)\s+[^\s,;"']+/gi, '$1 [REDACTED]')
    .replace(/\b(password|passwd|pwd|secret|(?:access[_-]?|refresh[_-]?)?token|community|authorization|api[_-]?key)\b["']?\s*[:=]\s*(?:"(?:\\.|[^"\\])*(?:"|$)|'(?:\\.|[^'\\])*(?:'|$)|[^\s,;]+)/gi, '$1=[REDACTED]')
    .replace(/\b(password|passwd|pwd|secret|token|community|api[_-]?key)\s+(?:(?:cipher|simple|hash|read|write)\s+)?[^\s,;]+/gi, '$1 [REDACTED]')
    .replace(/(\w+:\/\/)[^\s/@]+:[^\s/@]+@/g, '$1[REDACTED]@')
  const cell = (value: unknown) => {
    const original = String(value ?? '')
    const safe = /^[\s\p{Cc}\p{Cf}]*[=+@-]/u.test(original) ? `'${original}` : original
    return `"${safe.replaceAll('"', '""')}"`
  }
  const iso = (at: number) => new Date(at).toISOString()
  const rows: unknown[][] = [['scope', 'page', 'page_size', 'total_matching', 'loaded_at', 'filter_state', 'filter_device_id', 'filter_kind', 'filter_from_ms', 'filter_to_ms',
    'record_type', 'fault_id', 'device_id', 'profile_id', 'site_id', 'kind', 'resource_key', 'severity', 'state', 'occurrences', 'first_seen', 'last_seen', 'source_type', 'evidence_id', 'detail_redacted',
    'history_at', 'history_action', 'history_actor_user_id', 'history_evidence_id', 'history_detail_redacted', 'source_authority', 'captured_scope_key']]
  const prefix = ['CURRENT_PAGE_LOADED_QUERY_ONLY', data.page, data.pageSize, data.total, scope.at, scope.filters.state, scope.filters.deviceId, scope.filters.kind, scope.filters.fromMs, scope.filters.toMs]
  for (const item of data.items) {
    const facts = [item.id, item.deviceId, item.profileId, item.siteId, item.kind, item.resourceKey, item.severity, item.state, item.occurrences, iso(item.firstSeenMs), iso(item.lastSeenMs), item.evidenceType, item.evidenceId, redact(item.detail)]
    rows.push([...prefix, 'EVENT', ...facts, '', '', '', '', '', item.sourceAuthority, item.capturedScopeKey])
    for (const entry of item.history) rows.push([...prefix, 'HISTORY', ...facts, iso(entry.atMs), entry.action, entry.actorUserId, entry.evidenceId, redact(entry.detail), item.sourceAuthority, item.capturedScopeKey])
  }
  const url = URL.createObjectURL(new Blob(['\uFEFF' + rows.map(row => row.map(cell).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = `fault-history-page-${data.page}.csv`; anchor.click(); URL.revokeObjectURL(url)
}
function filterDevice(item: FaultIncident) {
  if (busy.value) return
  deviceId.value = item.deviceId; expanded.value = ''; void load()
}
function resetFilters() {
  if (busy.value) return
  state.value = ''; deviceId.value = ''; kind.value = ''; from.value = ''; to.value = ''; expanded.value = ''; void load()
}
function sourceEvidence(item: FaultIncident): { href: string; label: string } | null {
  if (!item.evidenceId) return null
  const id = encodeURIComponent(item.evidenceId)
  if (item.evidenceType === 'TASK_RUN') return { href: `/api/system/tasks/${id}`, label: '查看来源任务证据' }
  if (item.evidenceType === 'DIAGNOSTIC') return { href: `/api/system/diagnostics/runs/${id}`, label: '查看来源诊断证据' }
  if (item.evidenceType === 'WIRELESS_RUNTIME') return { href: `/api/system/wireless/ap-resources/${id}/runtime-state`, label: '查看 AP 运行证据' }
  return null
}
onMounted(() => { void load() })
onDeactivated(() => { active = false; generation++; result.value = null; loadedScope.value = null; busy.value = false; expanded.value = ''; notice.value = ''; error.value = '' })
onActivated(() => { if (!active) { active = true; void load() } })
watch(() => props.identityKey, () => {
  generation++; result.value = null; loadedScope.value = null; expanded.value = ''; notice.value = ''; error.value = ''; busy.value = false
  state.value = ''; deviceId.value = ''; kind.value = ''; from.value = ''; to.value = ''
  if (active) void load()
}, { flush: 'sync' })
onBeforeUnmount(() => { generation++ })
</script>

<template>
  <section class="card fault-center" aria-label="持久故障中心">
    <header class="card__header"><h2>故障中心</h2><div><button :disabled="busy || !!error || !result?.items.length" @click="exportCsv">导出当前页故障与历史 CSV</button><button :disabled="busy" @click="load()">刷新故障历史</button></div></header>
    <div class="card__body">
      <p>事件按设备、Site、故障类型与资源聚合。确认表示已知悉；恢复必须通过更新的有效证据核验。</p>
      <p>Ping 连续 3 次有效丢失形成丢包事件；同目标末尾连续 3 次有效应答且均晚于事件才可恢复。累计丢包率保留，不代表设备离线。</p>
      <p>运行中的 Long Ping 按真实样本同步故障、恢复与复发，无须等待任务结束。暂停和取消不提供恢复证据；ICMP 与 TCP 端口分别核验。</p>
      <form class="fault-center__filters" @submit.prevent="load()">
        <label>故障状态 <select v-model="state" aria-label="故障状态"><option value="">全部</option><option>OPEN</option><option>ACKNOWLEDGED</option><option>PROCESSING</option><option>RESOLVED</option><option>REOPENED</option></select></label>
        <label>设备 ID <input v-model.trim="deviceId" aria-label="设备 ID" placeholder="精确设备 ID"></label>
        <label>故障类型 <select v-model="kind" aria-label="故障类型"><option value="">全部</option><option>INTERFACE_DOWN</option><option>LLDP_NEIGHBOR_LOST</option><option>COLLECTION_FAILURE</option><option>EVIDENCE_STALE</option><option>PROBE_PACKET_LOSS</option><option>TRACEROUTE_ANOMALY</option><option>AP_OFFLINE</option><option>AP_CONFLICT</option></select></label>
        <label>起始时间 <input v-model="from" aria-label="起始时间" type="datetime-local"></label><label>截止时间 <input v-model="to" aria-label="截止时间" type="datetime-local"></label>
        <button :disabled="busy" type="submit">查询故障</button><button :disabled="busy" type="button" @click="resetFilters">重置故障筛选</button><button v-if="props.canRecover" :disabled="busy" type="button" @click="synchronize">同步当前证据</button>
      </form>
      <p v-if="busy" role="status">正在处理故障记录…</p><p v-if="error" role="alert">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
      <template v-if="result">
        <p v-if="result.summary" aria-label="筛选范围故障统计">待处理 {{ result.summary.open }} · 已确认 {{ result.summary.acknowledged }} · 处理中 {{ result.summary.processing }} · 已恢复 {{ result.summary.resolved }} · 复发 {{ result.summary.reopened }} · 涉及设备 {{ result.summary.affectedDevices }}（当前筛选全部匹配记录）</p>
        <p>历史 {{ result.total }} 项 · 第 {{ result.page }} 页 · 自动补采默认关闭</p>
        <p>CSV 仅含本次已加载页和已应用筛选，含该页事件的保留历史；编辑筛选后请先查询。自由文本按凭据模式脱敏，分享前请复核。</p>
        <p v-if="!result.items.length">当前筛选没有故障记录。</p>
        <div v-else class="fault-center__table"><table><thead><tr><th>故障 / 资源</th><th>设备 / Site</th><th>生命周期</th><th>首次 / 最近观测</th><th>证据 / 操作</th></tr></thead>
          <tbody><template v-for="item in result.items" :key="item.id"><tr>
            <td><strong>{{ item.kind }}</strong><small>{{ item.resourceKey }}</small><small>{{ item.detail }}</small></td>
            <td>{{ item.deviceId }}<small>{{ item.siteId }}</small><button :disabled="busy" @click="filterDevice(item)">仅看此设备故障</button><RouterLink v-if="item.deviceId && item.profileId" :to="{ path: '/config-backups', query: { deviceId: item.deviceId } }">查看此设备配置历史</RouterLink></td><td>{{ item.state }}<small>{{ item.occurrences }} 次有效观测</small></td>
            <td>{{ time(item.firstSeenMs) }}<small>{{ time(item.lastSeenMs) }}</small></td>
            <td><small>{{ item.evidenceType }}: {{ item.evidenceId }}</small><a v-if="sourceEvidence(item)" :href="sourceEvidence(item)!.href" target="_blank" rel="noopener">{{ sourceEvidence(item)!.label }}</a><button :disabled="busy" @click="expanded = expanded === item.id ? '' : item.id">查看处理历史</button>
              <template v-if="props.canRecover && item.state !== 'RESOLVED'"><button :disabled="busy || !['OPEN', 'REOPENED'].includes(item.state)" @click="acknowledge(item)">确认故障</button><button :disabled="busy || item.state === 'PROCESSING'" @click="process(item)">开始处理</button><button :disabled="busy" @click="verify(item)">核验恢复</button></template></td>
          </tr><tr v-if="expanded === item.id"><td colspan="5">
            <div v-if="item.sourceAuthority">来源 {{ item.sourceAuthority }}<details><summary>捕获范围</summary><span class="mono">{{ item.capturedScopeKey || 'UNSCOPED' }}</span></details></div><ol><li v-for="(entry, index) in item.history" :key="index">{{ time(entry.atMs) }} · {{ entry.action }} · {{ entry.actorUserId }}<small>{{ entry.detail }} · 证据 {{ entry.evidenceId }}</small></li></ol>
            <p v-if="policy(item)">补采策略 {{ policy(item)?.state }} · {{ policy(item)?.attempts }}/{{ policy(item)?.maximumAttempts }} 轮 · 下次 {{ time(policy(item)?.nextRunMs ?? null) }}<small v-if="policy(item)?.lastError">{{ policy(item)?.lastError }}</small><a v-if="policy(item)?.planId" :href="`/api/system/h3c/task-plans/${encodeURIComponent(policy(item)?.planId ?? '')}/report`" target="_blank" rel="noopener">查看真实补采计划</a></p>
            <template v-if="props.canRecover && item.state !== 'RESOLVED'">
              <p>重新连接并采集版本和接口；邻居消失故障同时读取完整 LLDP 表。最多 3 轮，每轮最多 3 次瞬态连接探测；认证或密钥失败停止。启用后等待完整间隔。</p>
              <label>间隔分钟 <input v-model.number="interval" type="number" min="5" max="1440"></label><label>最多轮数 <input v-model.number="attempts" type="number" min="1" max="3"></label>
              <button :disabled="busy || policy(item)?.enabled || ['RUNNING', 'AWAITING_RECOLLECTION'].includes(policy(item)?.state ?? '')" @click="configure(item, true)">启用有限只读补采</button><button v-if="policy(item)?.enabled" :disabled="busy" @click="configure(item, false)">停止后续补采</button>
            </template>
          </td></tr></template></tbody></table></div>
        <nav aria-label="故障历史分页"><button :disabled="busy || result.page <= 1" @click="load(result.page - 1)">上一页</button><button :disabled="busy || !result.hasMore" @click="load(result.page + 1)">下一页</button></nav>
        <details><summary>范围与保留限制</summary><p v-for="item in result.limitations" :key="item">{{ item }}</p></details>
      </template>
    </div>
  </section>
</template>

<style scoped>
.fault-center { margin-top: 18px; }
.fault-center__filters { display: flex; flex-wrap: wrap; align-items: end; gap: 8px; }
label { display: inline-flex; flex-direction: column; gap: 4px; margin-right: 8px; font-size: 13px; }
input, select, button { padding: 6px 9px; max-width: 100%; }
button { margin: 4px; }
button:disabled { opacity: .55; }
input[type=number] { width: 95px; }
.fault-center__table { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; table-layout: fixed; }
td, th { text-align: left; vertical-align: top; padding: 9px; border-bottom: 1px solid var(--el-border-color, #ddd); overflow-wrap: anywhere; }
small { display: block; margin-top: 5px; overflow-wrap: anywhere; }
td > a { display: block; margin: 7px 4px; }
ol { max-height: 260px; overflow: auto; }
li { margin-bottom: 8px; }
[role=alert] { color: var(--el-color-danger, #b42318); }
</style>
