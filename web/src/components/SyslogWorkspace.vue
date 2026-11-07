<script setup lang="ts">
import { onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import { getHealth, request, type HealthSnapshot } from '../api/client'
interface Entry { receivedMs: number; senderIp: string; senderPort: number; facility: number | null; severity: number | null; message: string; rawReference: string; line: number }
interface Result { items: Entry[]; scannedSegments: number; scannedRecords: number; unavailableSegments: number; partial: boolean; sourceState: string }
const props = defineProps<{ identityKey?: string }>()
const senderIp = ref(''), text = ref(''), maximumSeverity = ref(''), hours = ref('24')
const result = ref<Result | null>(null), loading = ref(false), error = ref('')
const health = ref<HealthSnapshot | null>(null), healthError = ref(''), healthLoading = ref(false)
let generation = 0
let healthGeneration = 0
let active = true
function clearEvidence(): void {
  generation++; healthGeneration++
  result.value = null; error.value = ''; loading.value = false
  health.value = null; healthError.value = ''; healthLoading.value = false
}
watch(() => props.identityKey, () => { clearEvidence(); if (active) void refreshReceiver() })
async function refreshReceiver(): Promise<void> {
  const current = ++healthGeneration
  healthLoading.value = true; healthError.value = ''; health.value = null
  try { const value = await getHealth(); if (current === healthGeneration) health.value = value }
  catch (e) { if (current === healthGeneration) healthError.value = e instanceof Error ? e.message : '接收状态不可用' }
  finally { if (current === healthGeneration) healthLoading.value = false }
}
async function query(): Promise<void> {
  const current = ++generation
  loading.value = true; error.value = ''; result.value = null
  const toMs = Date.now()
  try {
    const response = await request<Result>('/api/system/syslog/query', { method: 'POST', body: JSON.stringify({
      fromMs: toMs - Number(hours.value) * 3600000, toMs, senderIp: senderIp.value.trim() || null,
      text: text.value.trim() || null, maximumSeverity: maximumSeverity.value === '' ? null : Number(maximumSeverity.value), limit: 200,
    }) })
    if (current === generation) result.value = response
  } catch (e) { if (current === generation) error.value = e instanceof Error ? e.message : 'Syslog 查询失败' }
  finally { if (current === generation) loading.value = false }
}
function exportCsv(): void {
  if (!result.value) return
  const cell = (value: unknown) => {
    const original = String(value ?? '')
    const safe = /^[\s]*[=+@-]/.test(original) ? `'${original}` : original
    return `"${safe.replaceAll('"', '""')}"`
  }
  const rows = [['接收时间', '来源 IP', '端口', 'Facility', 'Severity', '消息', 'Raw', '行'],
    ...result.value.items.map(item => [new Date(item.receivedMs).toISOString(), item.senderIp, item.senderPort, item.facility, item.severity, item.message, item.rawReference, item.line])]
  const url = URL.createObjectURL(new Blob(['\uFEFF' + rows.map(row => row.map(cell).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }))
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'syslog-query.csv'; anchor.click(); URL.revokeObjectURL(url)
}
onMounted(refreshReceiver)
onDeactivated(() => { active = false; clearEvidence() })
onActivated(() => { if (!active) { active = true; void refreshReceiver() } })
onBeforeUnmount(() => { active = false; clearEvidence() })
</script>

<template>
  <section class="card syslog-workspace">
    <div class="card__header"><h2>Syslog 查询</h2><el-button :disabled="!result?.items.length" @click="exportCsv">导出当前结果</el-button></div>
    <div class="card__body">
      <p>查询本机接收源已封存的日志，包含原始消息及来源引用。接收日志最长约 5 秒后封存可查；UDP 接收默认关闭。</p>
      <div class="receiver-state" aria-label="Syslog 接收状态">
        <el-button :loading="healthLoading" @click="refreshReceiver">刷新接收状态</el-button>
        <p v-if="healthError" role="alert">接收状态读取失败：{{ healthError }}</p>
        <template v-if="health">
          <p>接收{{ health.syslogEnabled ? '已启用' : '未启用' }} · {{ health.collectionState }} · 已接收 {{ health.udpReceived }} 条 / 已落盘 {{ health.rawFlushedRecords }} 条 / 丢弃 {{ health.udpDropped ?? '未知' }} 条</p>
          <p v-if="health.collectionError || !health.rawWriteAvailable || (health.udpDropped ?? 0) > 0" role="alert">{{ health.collectionError || (!health.rawWriteAvailable ? '日志存储不可写' : '发生日志丢弃，请检查服务负载和存储') }}</p>
        </template>
      </div>
      <form class="syslog-filters" @submit.prevent="query">
        <label>时间范围<select v-model="hours" aria-label="Syslog 时间范围"><option value="1">最近 1 小时</option><option value="24">最近 24 小时</option><option value="168">最近 7 天</option></select></label>
        <label>来源 IP<input v-model="senderIp" maxlength="64" aria-label="Syslog 来源 IP" placeholder="完整 IP，留空为全部" /></label>
        <label>最高级别<select v-model="maximumSeverity" aria-label="Syslog 最高级别"><option value="">全部 / 未知</option><option v-for="n in 8" :key="n" :value="String(n - 1)">{{ n - 1 }} 及以上紧急程度</option></select></label>
        <label>消息内容<input v-model="text" maxlength="256" aria-label="Syslog 消息内容" /></label>
        <el-button native-type="submit" :loading="loading">查询日志</el-button>
      </form>
      <p v-if="error" role="alert">{{ error }}</p>
      <template v-if="result">
        <p role="status">{{ result.items.length }} 条结果 · 扫描 {{ result.scannedSegments }} 个分段 / {{ result.scannedRecords }} 条记录</p>
        <p v-if="result.partial" role="alert">结果不完整：达到查询上限或部分日志不可读。请缩短时间范围；不可用项 {{ result.unavailableSegments }}。</p>
        <p v-if="!result.items.length">当前条件没有已封存日志。请检查接收设置、来源和时间范围。</p>
        <div v-else class="syslog-table"><table><thead><tr><th>接收时间</th><th>来源 / 端口</th><th>Facility / Severity</th><th>消息 / Raw 引用</th></tr></thead>
          <tbody><tr v-for="entry in result.items" :key="`${entry.rawReference}:${entry.line}`"><td>{{ new Date(entry.receivedMs).toLocaleString() }}</td><td>{{ entry.senderIp }}:{{ entry.senderPort }}</td><td>{{ entry.facility ?? '未知' }} / {{ entry.severity ?? '未知' }}</td><td><pre>{{ entry.message }}</pre><small>{{ entry.rawReference }}:{{ entry.line }}</small></td></tr></tbody></table></div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.syslog-workspace { margin-bottom: 14px; }
.syslog-filters { display:flex; flex-wrap:wrap; gap:12px; align-items:end; }
label { display:flex; flex-direction:column; gap:6px; }
input, select { max-width:240px; padding:7px; }
.syslog-table { overflow:auto; } table { width:100%; border-collapse:collapse; } th,td { padding:8px; text-align:left; vertical-align:top; border-bottom:1px solid var(--nc-border); }
pre { white-space:pre-wrap; overflow-wrap:anywhere; margin:0; min-width:240px; max-width:700px; } small { overflow-wrap:anywhere; }
</style>
