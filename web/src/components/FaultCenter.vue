<script setup lang="ts">
import { onActivated, onBeforeUnmount, onDeactivated, onMounted, ref } from 'vue'
import { acknowledgeFault, getFaults, setFaultPolicy, syncFaults, verifyFault, type FaultIncident, type FaultResult } from '../api/faults'

const props = withDefaults(defineProps<{ canRecover?: boolean }>(), { canRecover: false })
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
let generation = 0
let active = true
const time = (at: number | null) => at ? new Date(at).toLocaleString() : '—'
async function load(page = 1) {
  const current = ++generation
  busy.value = true; error.value = ''
  try {
    const data = await getFaults(page, { state: state.value, deviceId: deviceId.value, kind: kind.value,
      fromMs: from.value ? new Date(from.value).getTime() : undefined, toMs: to.value ? new Date(to.value).getTime() : undefined })
    if (current === generation) result.value = data
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
const verify = (item: FaultIncident) => action(async () => { const report = await verifyFault(item.id); return `${report.state} · ${report.detail}` })
const synchronize = () => action(async () => { await syncFaults(); return '已同步第 1 页授权设备证据；其他页由后台轮巡。' })
const configure = (item: FaultIncident, enabled: boolean) => action(async () => {
  const report = await setFaultPolicy(item, enabled, interval.value, attempts.value)
  return `只读补采策略：${report.state}；连接和入队均不代表业务恢复。`
})
const policy = (item: FaultIncident) => result.value?.policies.find(p => p.deviceId === item.deviceId)
onMounted(() => { void load() })
onDeactivated(() => { active = false; generation++; result.value = null; busy.value = false; expanded.value = ''; notice.value = ''; error.value = '' })
onActivated(() => { if (!active) { active = true; void load() } })
onBeforeUnmount(() => { generation++ })
</script>

<template>
  <section class="card fault-center" aria-label="持久故障中心">
    <header class="card__header"><h2>故障中心</h2><button :disabled="busy" @click="load()">刷新故障历史</button></header>
    <div class="card__body">
      <p>事件按设备、Site、故障类型与资源聚合。确认表示已知悉；恢复必须通过更新的有效证据核验。</p>
      <form class="fault-center__filters" @submit.prevent="load()">
        <label>故障状态 <select v-model="state" aria-label="故障状态"><option value="">全部</option><option>OPEN</option><option>ACKNOWLEDGED</option><option>RESOLVED</option></select></label>
        <label>设备 ID <input v-model.trim="deviceId" aria-label="设备 ID" placeholder="精确设备 ID"></label>
        <label>故障类型 <select v-model="kind" aria-label="故障类型"><option value="">全部</option><option>INTERFACE_DOWN</option><option>COLLECTION_FAILURE</option><option>EVIDENCE_STALE</option><option>PROBE_PACKET_LOSS</option><option>AP_OFFLINE</option><option>AP_CONFLICT</option></select></label>
        <label>起始时间 <input v-model="from" aria-label="起始时间" type="datetime-local"></label><label>截止时间 <input v-model="to" aria-label="截止时间" type="datetime-local"></label>
        <button :disabled="busy" type="submit">查询故障</button><button v-if="props.canRecover" :disabled="busy" type="button" @click="synchronize">同步当前证据</button>
      </form>
      <p v-if="busy" role="status">正在处理故障记录…</p><p v-if="error" role="alert">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
      <template v-if="result">
        <p>历史 {{ result.total }} 项 · 第 {{ result.page }} 页 · 自动补采默认关闭</p>
        <p v-if="!result.items.length">当前筛选没有故障记录。</p>
        <div v-else class="fault-center__table"><table><thead><tr><th>故障 / 资源</th><th>设备 / Site</th><th>生命周期</th><th>首次 / 最近观测</th><th>证据 / 操作</th></tr></thead>
          <tbody><template v-for="item in result.items" :key="item.id"><tr>
            <td><strong>{{ item.kind }}</strong><small>{{ item.resourceKey }}</small><small>{{ item.detail }}</small></td>
            <td>{{ item.deviceId }}<small>{{ item.siteId }}</small></td><td>{{ item.state }}<small>{{ item.occurrences }} 次有效观测</small></td>
            <td>{{ time(item.firstSeenMs) }}<small>{{ time(item.lastSeenMs) }}</small></td>
            <td><small>{{ item.evidenceType }}: {{ item.evidenceId }}</small><button :disabled="busy" @click="expanded = expanded === item.id ? '' : item.id">查看处理历史</button>
              <template v-if="props.canRecover && item.state !== 'RESOLVED'"><button :disabled="busy || item.state !== 'OPEN'" @click="acknowledge(item)">确认故障</button><button :disabled="busy" @click="verify(item)">核验恢复</button></template></td>
          </tr><tr v-if="expanded === item.id"><td colspan="5">
            <ol><li v-for="(entry, index) in item.history" :key="index">{{ time(entry.atMs) }} · {{ entry.action }} · {{ entry.actorUserId }}<small>{{ entry.detail }} · 证据 {{ entry.evidenceId }}</small></li></ol>
            <p v-if="policy(item)">补采策略 {{ policy(item)?.state }} · {{ policy(item)?.attempts }}/{{ policy(item)?.maximumAttempts }} 轮 · 下次 {{ time(policy(item)?.nextRunMs ?? null) }}<small v-if="policy(item)?.lastError">{{ policy(item)?.lastError }}</small><a v-if="policy(item)?.planId" :href="`/api/system/h3c/task-plans/${encodeURIComponent(policy(item)?.planId ?? '')}/report`" target="_blank" rel="noopener">查看真实补采计划</a></p>
            <template v-if="props.canRecover && item.state !== 'RESOLVED'">
              <p>仅重新连接并采集版本和接口，不修改配置。最多 3 轮，每轮最多 3 次瞬态连接探测；认证或密钥失败停止。启用后等待完整间隔。</p>
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
ol { max-height: 260px; overflow: auto; }
li { margin-bottom: 8px; }
[role=alert] { color: var(--el-color-danger, #b42318); }
</style>
