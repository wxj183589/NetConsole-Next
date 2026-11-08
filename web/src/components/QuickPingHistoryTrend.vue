<script setup lang="ts">
import { computed, onBeforeUnmount, onDeactivated, ref, watch } from 'vue'
import { ApiRequestError, getNetworkDiagnostics, type NetworkDiagnosticRun, type NetworkDiagnosticRunSummary } from '../api/client'
import { buildQuickPingTrends, quickPingTrendCsv } from '../api/quickPingTrend'

const props = defineProps<{ history: NetworkDiagnosticRunSummary[]; identityKey: string }>()
const runs = ref<NetworkDiagnosticRun[]>([])
const failedRuns = ref<string[]>([])
const loading = ref(false)
const loadedMs = ref(0)
const error = ref('')
const selectedSite = ref(''), selectedDevice = ref(''), selectedActor = ref('')
let sequence = 0
const candidates = computed(() => props.history.filter(run => run.kinds.some(kind => kind === 'QUICK_PING' || kind === 'LONG_PING')).slice(0, 20))
const trends = computed(() => buildQuickPingTrends(runs.value))
const sites = computed(() => [...new Set(trends.value.groups.map(group => group.siteId))].sort())
const actors = computed(() => [...new Set(trends.value.groups.map(group => group.actor))].sort())
const devices = computed(() => [...new Map(trends.value.groups.map(group => [group.deviceId, group.deviceName])).entries()])
const visible = computed(() => trends.value.groups.filter(group => (!selectedSite.value || group.siteId === selectedSite.value) &&
  (!selectedDevice.value || group.deviceId === selectedDevice.value) && (!selectedActor.value || group.actor === selectedActor.value)))
const metric = (value: number | null) => value === null ? 'UNKNOWN' : String(value)
const kindLabel = (kind: string) => kind === 'LONG_PING' ? 'Long Ping 长期采样' : 'Quick Ping 连续采样'
const time = (ms: number) => new Date(ms).toLocaleString('zh-CN', { hour12: false })
function clear() {
  ++sequence; runs.value = []; failedRuns.value = []; loading.value = false; loadedMs.value = 0; error.value = ''
  selectedSite.value = ''; selectedDevice.value = ''; selectedActor.value = ''
}
async function load() {
  clear()
  if (!props.identityKey || !candidates.value.length) return
  const request = sequence
  const ids = [...new Set(candidates.value.map(run => run.id))]
  loading.value = true
  const next: NetworkDiagnosticRun[] = [], failed: string[] = []
  try {
    for (let offset = 0; offset < ids.length; offset += 4) {
      const batch = ids.slice(offset, offset + 4)
      const results = await Promise.allSettled(batch.map(id => getNetworkDiagnostics(id)))
      if (request !== sequence) return
      for (const [index, result] of results.entries()) {
        if (result.status === 'rejected') {
          if (result.reason instanceof ApiRequestError && [401, 403].includes(result.reason.status)) throw result.reason
          failed.push(batch[index]!); continue
        }
        if (result.value.id !== batch[index]) { failed.push(batch[index]!); continue }
        next.push(result.value)
      }
    }
    if (request !== sequence) return
    runs.value = next; failedRuns.value = failed; loadedMs.value = Date.now()
  } catch { if (request === sequence) { runs.value = []; error.value = '趋势历史不可访问，请刷新历史并核对当前权限。' } }
  finally { if (request === sequence) loading.value = false }
}
function exportCsv() {
  if (loading.value || !loadedMs.value || !visible.value.length) return
  const blob = new Blob([quickPingTrendCsv(visible.value, { loadedMs: loadedMs.value, failedRuns: failedRuns.value })], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob), anchor = document.createElement('a')
  anchor.href = url; anchor.download = `ping-history-${loadedMs.value}.csv`; anchor.click(); URL.revokeObjectURL(url)
}
watch(() => [props.identityKey, props.history], clear)
onDeactivated(clear)
onBeforeUnmount(clear)
</script>

<template>
  <section class="card quick-ping-history" data-testid="quick-ping-history-trend">
    <div class="card__header"><div><h2>Quick / Long Ping 历史趋势</h2><small>相同设备、Site、执行账号、采样类型、协议和目标 IP/端口分别统计</small></div><el-button :disabled="loading || !candidates.length" @click="load">{{ loading ? '正在读取趋势' : '加载 Quick / Long Ping 趋势' }}</el-button></div>
    <div class="card__body">
      <p class="muted">只读最近 50 条历史中的最多 20 个 Quick / Long Ping 任务，不发起探测。RTT 由全部已回复原始样本合并加权；P95 按回复延迟排序取第 ceil(95% × 回复数) 个值，不包含丢失样本。丢失率分母仅含已发送样本，未发送与缺失证据为 UNKNOWN；暂停期间未执行的采样不计丢失，运行中、正在暂停与已暂停的任务暂不纳入汇总。TCP 可达不能验证无线业务恢复。</p>
      <p v-if="loading" role="status">正在读取已保存的 Quick / Long Ping 结果…</p>
      <p v-else-if="error" role="alert">{{ error }}</p>
      <template v-else-if="loadedMs">
        <p class="muted">截至 {{ time(loadedMs) }}；已读 {{ runs.length }} 个任务，读取失败 {{ failedRuns.length }} 个，运行中跳过 {{ trends.pending }} 项，身份缺失 {{ trends.missingIdentity }} 项。{{ candidates.length === 20 ? '已达 20 任务上限，范围可能不完整。' : '' }}</p>
        <p v-if="failedRuns.length" role="alert">部分历史不可读，统计仅涵盖已加载任务；不可读任务未知：{{ failedRuns.join(' / ') }}</p>
        <div class="toolbar trend-filters"><label class="form-field"><span>Site</span><select v-model="selectedSite" aria-label="趋势 Site"><option value="">全部授权 Site</option><option v-for="site in sites" :key="site">{{ site }}</option></select></label><label class="form-field"><span>设备</span><select v-model="selectedDevice" aria-label="趋势设备"><option value="">全部设备</option><option v-for="[id, name] in devices" :key="id" :value="id">{{ name }} · {{ id }}</option></select></label><label class="form-field"><span>执行账号</span><select v-model="selectedActor" aria-label="趋势执行账号"><option value="">全部账号（分别统计）</option><option v-for="actor in actors" :key="actor">{{ actor }}</option></select></label><el-button :disabled="!visible.length" @click="exportCsv">导出趋势 CSV</el-button></div>
        <article v-for="group in visible" :key="group.key" class="trend-group">
          <h3>{{ group.deviceName }} · {{ group.deviceId }} · {{ kindLabel(group.kind) }}</h3><p class="mono">{{ group.actor }} · {{ group.siteId }} · {{ group.protocol }} · {{ group.endpoint }}{{ group.port === null ? '' : ':' + group.port }}</p>
          <p>合计发送 {{ metric(group.sent) }} / 回复 {{ metric(group.received) }} / 丢失 {{ metric(group.lost) }} / 未发送 UNKNOWN {{ metric(group.unsent) }}；丢失率 {{ metric(group.lossPercent) }}%；RTT 最小 / 加权平均 / 最大 {{ metric(group.minMs) }} / {{ metric(group.averageMs) }} / {{ metric(group.maxMs) }} ms；P95 {{ metric(group.p95Ms) }} ms</p>
          <div class="table-wrap"><table class="data-table" aria-label="Quick / Long Ping 跨任务趋势"><thead><tr><th>时间 / 任务</th><th>状态</th><th>发送 / 回复 / 丢失 / 未发送</th><th>丢失率</th><th>平均 RTT</th><th>P95 RTT</th></tr></thead><tbody><tr v-for="point in group.points" :key="`${point.runId}:${point.itemId}`"><td>{{ time(point.capturedMs) }}<br /><span class="mono">{{ point.runId }}</span></td><td>{{ point.state }}</td><td>{{ metric(point.sent) }} / {{ metric(point.received) }} / {{ metric(point.lost) }} / {{ metric(point.unsent) }}</td><td>{{ metric(point.lossPercent) }}%</td><td>{{ metric(point.averageMs) }} ms</td><td>{{ metric(point.p95Ms) }} ms</td></tr></tbody></table></div>
        </article>
        <p v-if="!visible.length" class="detail-empty">当前范围暂无已结束的 Quick / Long Ping 趋势样本。</p>
      </template>
      <p v-else class="muted">{{ candidates.length ? '加载后查看跨任务变化与样本汇总。' : '当前已加载历史没有 Quick / Long Ping 任务。' }}</p>
    </div>
  </section>
</template>

<style scoped>
.quick-ping-history { margin-bottom: 14px; }
.trend-filters { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-end; }
.trend-filters .form-field { min-width: 160px; max-width: 100%; }
.trend-group { margin-top: 20px; padding-top: 12px; border-top: 1px solid var(--nc-border); overflow-wrap: anywhere; }
</style>
