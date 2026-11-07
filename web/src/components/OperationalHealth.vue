<script setup lang="ts">
import { onActivated, onBeforeUnmount, onDeactivated, onMounted, ref } from 'vue'
import { getOperationalHealth, getH3cTaskPlanReport, recoverOperationalDevice, type H3cTaskPlanReport, type OperationalHealthSnapshot, type OperationalRecoveryReport } from '../api/client'

const props = withDefaults(defineProps<{ canRecover?: boolean }>(), { canRecover: false })
const snapshot = ref<OperationalHealthSnapshot | null>(null)
const report = ref<OperationalRecoveryReport | null>(null)
const recollection = ref<H3cTaskPlanReport | null>(null)
const checking = ref(false)
const loading = ref(false)
const recovering = ref(false)
const error = ref('')
const selectedDevice = ref('')
let generation = 0
let active = true
async function load(page = 1) {
  const current = ++generation
  loading.value = true
  error.value = ''
  selectedDevice.value = ''
  report.value = null
  recollection.value = null
  try { const result = await getOperationalHealth(page); if (current === generation) snapshot.value = result }
  catch (cause) { if (current === generation) { snapshot.value = null; error.value = cause instanceof Error ? cause.message : '设备健康读取失败' } }
  finally { if (current === generation) loading.value = false }
}
async function recover() {
  const device = snapshot.value?.devices.find(item => item.deviceId === selectedDevice.value)
  if (!device?.profileId || !props.canRecover || recovering.value || loading.value) return
  const current = generation
  recovering.value = true
  error.value = ''
  report.value = null
  recollection.value = null
  try {
    const result = await recoverOperationalDevice({ deviceId: device.deviceId, expectedProfileId: device.profileId })
    if (current === generation) { report.value = result; if (result.planId) await readRecollection() }
  } catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '只读恢复失败' }
  finally { recovering.value = false }
}
async function readRecollection() {
  const planId = report.value?.planId
  if (!planId || checking.value) return
  const current = generation
  checking.value = true
  try { const result = await getH3cTaskPlanReport(planId); if (current === generation && report.value?.planId === planId) recollection.value = result }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '补采结果读取失败' }
  finally { checking.value = false }
}
function metric(item: { state: string; value: number | null } | null | undefined) { return item?.value == null ? 'UNKNOWN' : `${item.value} (${item.state})` }
function time(at: number | null | undefined) { return at ? new Date(at).toLocaleString() : '—' }
function evidenceUrl(event: OperationalHealthSnapshot['events'][number]) {
  if (event.evidenceType === 'TASK_RUN') return `/api/system/tasks/${encodeURIComponent(event.evidenceId)}`
  if (event.evidenceType === 'DIAGNOSTIC') return `/api/system/diagnostics/runs/${encodeURIComponent(event.evidenceId)}`
  if (event.evidenceType === 'WIRELESS_RUNTIME') return `/api/system/wireless/ap-resources/${encodeURIComponent(event.evidenceId)}/runtime-state`
  const profileId = snapshot.value?.devices.find(item => item.deviceId === event.deviceId)?.profileId
  return profileId ? `/api/system/h3c/profiles/${encodeURIComponent(profileId)}/facts?limit=1000` : undefined
}
onMounted(() => { void load() })
onDeactivated(() => { active = false; generation++; snapshot.value = null; report.value = null; recollection.value = null; selectedDevice.value = ''; loading.value = false })
onActivated(() => { if (!active) { active = true; void load() } })
onBeforeUnmount(() => { generation++ })
</script>

<template>
  <section class="card operational-health">
    <header class="card__header"><h2>设备健康与故障事件</h2><button :disabled="loading || recovering" @click="load(snapshot?.page ?? 1)">刷新设备健康</button></header>
    <div class="card__body">
      <p>健康证据窗口 5 分钟。UNKNOWN 表示缺少有效证据；采集失败不代表设备离线。</p>
      <p v-if="loading" role="status">正在读取授权设备健康…</p>
      <p v-if="error" role="alert">{{ error }}</p>
      <template v-if="snapshot">
        <p>当前页 {{ snapshot.page }} · 授权库存 {{ snapshot.totalDevices }} 台 · 读取时间 {{ time(snapshot.asOfMs) }}</p>
        <p v-if="!snapshot.devices.length">当前范围没有设备。</p>
        <div v-else class="operational-health__table">
          <table><thead><tr><th>设备 / Site</th><th>证据状态</th><th>CPU / 内存 %</th><th>Ping ms / 丢失 %</th><th>接口 DOWN</th><th>LLDP 邻居</th><th>最近采集证据</th></tr></thead>
            <tbody><tr v-for="device in snapshot.devices" :key="device.deviceId">
              <td><RouterLink :to="{ path: '/devices', query: { q: device.name } }">{{ device.name }}</RouterLink><br>{{ device.siteId }}</td>
              <td>{{ device.state }}<small v-for="warning in device.warnings" :key="warning">{{ warning }}</small></td>
              <td>{{ metric(device.cpuPercent) }} / {{ metric(device.memoryPercent) }}</td>
              <td>{{ metric(device.pingDelayMs) }} / {{ metric(device.packetLossPercent) }}</td>
              <td>{{ metric(device.interfaceDown) }}</td><td>{{ metric(device.lldpNeighbors) }}</td><td>{{ time(device.reachability.capturedMs) }}</td>
            </tr></tbody>
          </table>
        </div>
        <nav aria-label="设备健康分页"><button :disabled="loading || recovering || snapshot.page <= 1" @click="load(snapshot.page - 1)">上一页</button><button :disabled="loading || recovering || !snapshot.hasMore" @click="load(snapshot.page + 1)">下一页</button></nav>
        <h3>统一故障事件</h3>
        <p v-if="!snapshot.events.length">本页及限定证据窗口内没有故障事件。</p>
        <ul v-else class="operational-health__events"><li v-for="event in snapshot.events" :key="event.id"><strong>{{ event.kind }} · {{ event.state }}</strong> {{ time(event.atMs) }}<p>{{ event.detail }}</p><a v-if="evidenceUrl(event)" :href="evidenceUrl(event)" target="_blank" rel="noopener">{{ event.evidenceType }}: {{ event.evidenceId }}</a></li></ul>
        <template v-if="props.canRecover">
          <h3>受控只读恢复</h3>
          <p>只执行最多 3 次瞬态连接重试；认证和主机密钥失败立即停止。连接成功后提交版本、接口补采，不修改设备配置。</p>
          <label>恢复目标 <select v-model="selectedDevice" :disabled="recovering || loading"><option value="">请选择设备</option><option v-for="device in snapshot.devices.filter(item => item.profileId && item.state !== 'DISABLED')" :key="device.deviceId" :value="device.deviceId">{{ device.name }} · {{ device.siteId }}</option></select></label>
          <button :disabled="!selectedDevice || recovering || loading" @click="recover">{{ recovering ? '正在探测与补采…' : '执行只读恢复' }}</button>
        </template>
        <div v-if="report" role="status"><p>恢复结果：{{ report.state }}。连接成功与补采排队均不代表业务恢复完成。</p><ol><li v-for="attempt in report.attempts" :key="attempt.ordinal">第 {{ attempt.ordinal }} 次：{{ attempt.status }} · {{ time(attempt.atMs) }}</li></ol><a v-if="report.planId" :href="`/api/system/h3c/task-plans/${encodeURIComponent(report.planId)}/report`" target="_blank" rel="noopener">查看补采计划 {{ report.planId }}（{{ report.recollectionState }}）</a></div>
        <div v-if="report?.planId" aria-label="恢复补采结果"><button :disabled="checking" @click="readRecollection">读取补采结果</button><template v-if="recollection"><p>补采计划当前状态：{{ recollection.plan.state }}；需核验采集证据后判断业务恢复。</p><ul><li v-for="item in recollection.items" :key="item.item.ordinal">{{ item.run?.state || item.item.state }}<ul><li v-for="command in item.commands" :key="command.index">{{ command.command }} · {{ command.state }} · Raw={{ command.captureStatus || 'UNKNOWN' }} · Parse={{ command.parseStatus || 'UNKNOWN' }}</li></ul></li></ul></template></div>
        <details><summary>证据范围与限制</summary><p v-for="notice in snapshot.limitations" :key="notice">{{ notice }}</p></details>
      </template>
    </div>
  </section>
</template>

<style scoped>
.operational-health { margin-top: 18px; }
.operational-health__table { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; border-bottom: 1px solid var(--el-border-color, #ddd); padding: 10px; vertical-align: top; }
small { display: block; max-width: 240px; overflow-wrap: anywhere; }
.operational-health__events { max-height: 350px; overflow: auto; padding-left: 20px; }
.operational-health__events li { margin-bottom: 14px; }
button, select { padding: 6px 10px; margin: 5px; }
button:disabled { cursor: not-allowed; opacity: .55; }
[role="alert"] { color: var(--el-color-danger, #b42318); }
details { margin-top: 16px; }
</style>
