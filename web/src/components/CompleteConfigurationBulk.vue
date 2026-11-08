<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import ConfigurationRestoreBulkReadiness from './ConfigurationRestoreBulkReadiness.vue'
import { deleteConfigBackupSchedule, getDeviceSites, getWirelessAcDomains, setConfigBackupScheduleEnabled, type DeviceSite } from '../api/client'
import { createCompleteConfigurationBatch, createCompleteConfigurationSchedule, getCompleteConfigurationBatch, getCompleteConfigurationBatches, getCompleteConfigurationSchedules, resolveCompleteConfigurationTargets, runCompleteConfigurationSchedule, type CompleteConfigurationBatch, type CompleteConfigurationSchedule, type CompleteConfigurationTargetPreview } from '../api/completeConfiguration'

const props = defineProps<{ selectedProfileIds: string[] }>()
const emit = defineEmits<{ completed: []; inspect: [profileId: string] }>()
const selection = ref<'PROFILES' | 'SITE' | 'DOMAIN'>('PROFILES')
const siteId = ref(''); const domainId = ref('')
const sites = ref<DeviceSite[]>([]); const domains = ref<Array<{ id: string; displayName: string }>>([])
const preview = ref<CompleteConfigurationTargetPreview | null>(null)
const batch = ref<CompleteConfigurationBatch | null>(null)
const schedules = ref<CompleteConfigurationSchedule[]>([])
const viewingPlanId = ref('')
const history = ref<CompleteConfigurationBatch[]>([])
const historyId = ref('')
const interval = ref(1440); const busy = ref(false); const error = ref(''); const choicesError = ref('')
let sequence = 0; let pollSequence = 0; let timer: ReturnType<typeof setTimeout> | undefined; let disposed = false; let active = true
const failedProfiles = computed(() => batch.value?.items.filter(item => item.state === 'FAILED').map(item => item.profileId) ?? [])
const running = computed(() => !!batch.value && !batch.value.completedMs)
const canSubmit = computed(() => !busy.value && !running.value && !!preview.value?.profileIds.length)
function time(value?: number | null): string { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function state(value: string): string { return ({ QUEUED: '排队中', RUNNING: '执行中', SUCCEEDED: '已完成', FAILED: '失败', PARTIAL: '部分完成', NEVER_RUN: '尚未执行', BLOCKED: '无法执行', INTERRUPTED: '服务重启中断' } as Record<string, string>)[value] ?? value }
function failure(value?: string | null): string {
  if (value === 'CONFIG_COMPLETE_STORAGE_LIMIT') return '完整备份存储容量已满，已停止新增采集；历史仍可读取，请联系管理员调整容量。'
  if (value === 'CONFIG_COMPLETE_HISTORY_LIMIT') return '此设备完整备份数量已满，已停止新增采集；历史仍可读取，请联系管理员调整容量。'
  return value || ''
}
function resetPreview(): void { ++sequence; preview.value = null; error.value = ''; busy.value = false }
watch([selection, siteId, domainId, () => props.selectedProfileIds.join('\0')], resetPreview)
async function resolve(): Promise<void> {
  if (busy.value || running.value) return
  const current = ++sequence; busy.value = true; error.value = ''; preview.value = null
  const payload = selection.value === 'SITE' ? { siteId: siteId.value } : selection.value === 'DOMAIN' ? { domainId: domainId.value } : { profileIds: [...props.selectedProfileIds] }
  try { const next = await resolveCompleteConfigurationTargets(payload); if (current === sequence && !disposed) preview.value = next }
  catch { if (current === sequence && !disposed) error.value = '无法解析备份范围。请检查设备、Site／Domain 权限及授权模拟器连接档案。' }
  finally { if (current === sequence && !disposed) busy.value = false }
}
async function refreshPlans(): Promise<void> {
  const next = await getCompleteConfigurationSchedules(); if (!disposed) schedules.value = next
}
async function refreshHistory(): Promise<void> {
  if (busy.value || running.value) return
  busy.value = true; error.value = ''
  try { const next = await getCompleteConfigurationBatches(); if (!disposed) history.value = next }
  catch { if (!disposed) error.value = '批次历史暂不可读，请检查当前权限后重试。' }
  finally { if (!disposed) busy.value = false }
}
async function inspectHistory(): Promise<void> {
  if (busy.value || running.value || !historyId.value) return
  busy.value = true; error.value = ''
  try {
    const next = await getCompleteConfigurationBatch(historyId.value)
    if (disposed) return
    ++pollSequence; clearTimeout(timer); batch.value = next; viewingPlanId.value = ''
    if (!next.completedMs) void poll(next.id)
  } catch { if (!disposed) error.value = '批次已过期或权限已变化，请刷新批次历史。' }
  finally { if (!disposed) busy.value = false }
}
async function inspectPlan(plan: CompleteConfigurationSchedule): Promise<void> {
  if (busy.value || running.value || !plan.lastBatchId || plan.lastBatchAvailable === false) return
  busy.value = true; error.value = ''
  try {
    const next = await getCompleteConfigurationBatch(plan.lastBatchId)
    if (disposed) return
    ++pollSequence; clearTimeout(timer)
    batch.value = next; viewingPlanId.value = plan.id
    if (!next.completedMs) void poll(next.id)
  } catch { if (!disposed) error.value = '无法读取计划最近执行，记录可能已过期或当前权限已变化。' }
  finally { if (!disposed) busy.value = false }
}
async function poll(id: string): Promise<void> {
  clearTimeout(timer)
  const current = ++pollSequence
  await readProgress(id, current)
}
async function readProgress(id: string, current: number): Promise<void> {
  if (!active || disposed || current !== pollSequence) return
  try {
    const next = await getCompleteConfigurationBatch(id)
    if (!active || disposed || current !== pollSequence || batch.value?.id !== id) return
    batch.value = next
    if (next.completedMs) { emit('completed'); void refreshPlans().catch(() => {}); return }
    timer = setTimeout(() => { void readProgress(id, current) }, 1000)
  } catch { if (active && !disposed && current === pollSequence && batch.value?.id === id) error.value = '无法读取批量进度，请刷新进度或检查当前权限。' }
}
async function start(retry = false): Promise<void> {
  if (busy.value || running.value || (!retry && !canSubmit.value)) return
  const targets = retry ? [...failedProfiles.value] : [...preview.value!.profileIds]
  if (!targets.length) return
  busy.value = true; error.value = ''
  try {
    const next = await createCompleteConfigurationBatch(targets)
    if (disposed) return
    batch.value = next; viewingPlanId.value = ''; void poll(next.id)
  } catch { if (!disposed) error.value = '完整配置批量备份未能提交。请重新预览目标并检查权限。' }
  finally { if (!disposed) busy.value = false }
}
async function schedule(): Promise<void> {
  if (!canSubmit.value) return
  const targets = [...preview.value!.profileIds]; busy.value = true; error.value = ''
  try { await createCompleteConfigurationSchedule(targets, interval.value); await refreshPlans() }
  catch { if (!disposed) error.value = '定时计划未能创建，请检查目标权限后重试。' }
  finally { if (!disposed) busy.value = false }
}
async function runPlan(plan: CompleteConfigurationSchedule): Promise<void> {
  if (busy.value || running.value || plan.lastState === 'RUNNING') return
  busy.value = true; error.value = ''
  try {
    const next = await runCompleteConfigurationSchedule(plan.id)
    await refreshPlans()
    if (disposed) return
    if (next.lastBatchId) {
      const nextBatch = await getCompleteConfigurationBatch(next.lastBatchId)
      if (disposed) return
      batch.value = nextBatch; viewingPlanId.value = plan.id
      if (!nextBatch.completedMs) void poll(next.lastBatchId)
      else emit('completed')
    }
  } catch { if (!disposed) error.value = '计划未能立即执行。请检查当前权限、模块和设备状态，或等待已有批次完成。' }
  finally { if (!disposed) busy.value = false }
}
async function changePlan(operation: () => Promise<unknown>): Promise<void> {
  if (busy.value) return
  busy.value = true; error.value = ''
  try { await operation(); await refreshPlans() }
  catch { if (!disposed) error.value = '计划操作失败，请刷新后重试。' }
  finally { if (!disposed) busy.value = false }
}
onMounted(async () => {
  const results = await Promise.allSettled([getDeviceSites(), getWirelessAcDomains({ pageSize: 100 }), getCompleteConfigurationSchedules()])
  if (disposed) return
  if (results[0].status === 'fulfilled') sites.value = results[0].value
  if (results[1].status === 'fulfilled') domains.value = results[1].value.items
  if (results[2].status === 'fulfilled') schedules.value = results[2].value
  if (results.some(item => item.status === 'rejected')) choicesError.value = '部分范围或计划未能加载，可重试刷新；设备范围仍可独立预览。'
})
onActivated(() => { const resume = !active; active = true; if (resume && running.value && batch.value) void poll(batch.value.id) })
onDeactivated(() => { active = false; ++pollSequence; clearTimeout(timer) })
onBeforeUnmount(() => { disposed = true; active = false; ++sequence; ++pollSequence; clearTimeout(timer) })
</script>

<template>
  <section class="card" aria-label="完整配置批量与定时备份">
    <div class="card__header"><h2>完整配置批量与定时备份</h2></div>
    <div class="card__body">
      <p class="muted">完整原件保存在服务私有目录；页面与下载只提供脱敏副本。当前仅支持授权模拟器，整机恢复仍受设备能力验证限制。</p>
      <div class="toolbar"><el-button :disabled="busy || running" @click="refreshHistory">刷新完整备份批次历史</el-button><select v-model="historyId" aria-label="完整备份批次历史" :disabled="busy || running"><option value="">选择最近 50 个可读批次</option><option v-for="item in history" :key="item.id" :value="item.id">{{ time(item.createdMs) }} · {{ state(item.state) }} · {{ item.succeeded }}/{{ item.total }} · {{ item.id }}</option></select><el-button :disabled="busy || running || !historyId" @click="inspectHistory">查看批次结果</el-button></div>
      <p class="muted">最近 128 个终态批次明细可跨重启查询。每台配置版本长期保留，批次展示上限不会删除配置。</p>
      <div class="toolbar">
        <label class="form-field"><span>备份范围</span><select v-model="selection" aria-label="完整配置备份范围" :disabled="busy || running"><option value="PROFILES">上方所选设备（{{ selectedProfileIds.length }} 台）</option><option value="SITE">按 Site</option><option value="DOMAIN">按 AC Domain</option></select></label>
        <label v-if="selection === 'SITE'" class="form-field"><span>Site</span><select v-model="siteId" aria-label="完整配置 Site" :disabled="busy || running"><option value="">请选择 Site</option><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.name }}</option></select></label>
        <label v-if="selection === 'DOMAIN'" class="form-field"><span>AC Domain</span><select v-model="domainId" aria-label="完整配置 Domain" :disabled="busy || running"><option value="">请选择 Domain</option><option v-for="domain in domains" :key="domain.id" :value="domain.id">{{ domain.displayName }}</option></select></label>
        <el-button :disabled="busy || running" @click="resolve">预览完整备份目标</el-button>
      </div>
      <p v-if="choicesError" class="data-quality-note" role="status">{{ choicesError }}</p>
      <p v-if="error" class="data-quality-note" role="alert">{{ error }}</p>
      <div v-if="preview" aria-label="完整备份目标预览">
        <p class="muted">已确认 {{ preview.items.length }} 台。计划固定使用这些设备，之后新增的 Site／Domain 成员不会自动加入。</p>
        <div class="table-wrap"><table class="data-table"><thead><tr><th>设备</th><th>Site</th><th>管理地址</th><th>型号</th></tr></thead><tbody><tr v-for="item in preview.items" :key="item.profileId"><td>{{ item.name }}</td><td>{{ item.site }}</td><td>{{ item.address }}</td><td>{{ item.model || '待采集' }}</td></tr></tbody></table></div>
        <div class="toolbar"><el-button :disabled="!canSubmit" @click="start(false)">执行完整批量备份</el-button><label class="form-field"><span>定时周期</span><select v-model="interval" aria-label="完整备份周期" :disabled="busy"><option :value="60">每小时</option><option :value="1440">每天</option><option :value="10080">每周</option></select></label><el-button :disabled="!canSubmit" @click="schedule">创建完整配置计划</el-button></div>
      </div>
      <div v-if="batch" aria-label="完整配置批量进度">
        <p v-if="viewingPlanId" class="muted">计划 {{ viewingPlanId }} 最近执行 · 批次 {{ batch.id }}</p>
        <p>{{ state(batch.state) }} · {{ batch.completed }}/{{ batch.total }} · 成功 {{ batch.succeeded }} · 失败 {{ batch.failed }}</p>
        <el-button :disabled="busy || running || !failedProfiles.length" @click="start(true)">仅重试失败设备</el-button><el-button :disabled="busy" @click="poll(batch.id)">刷新完整备份进度</el-button>
        <div class="table-wrap"><table class="data-table"><thead><tr><th>设备档案</th><th>状态</th><th>结果</th><th>操作</th></tr></thead><tbody><tr v-for="item in batch.items" :key="item.profileId"><td>{{ preview?.items.find(target => target.profileId === item.profileId)?.name || item.profileId }}</td><td>{{ state(item.state) }}</td><td>{{ failure(item.error) || item.snapshotId || '—' }}</td><td><el-button v-if="item.snapshotId" text @click="emit('inspect', item.profileId)">查看完整历史</el-button></td></tr></tbody></table></div>
      </div>
      <h3>完整配置计划</h3>
      <p class="muted">创建后经过一个周期自动执行；也可立即执行一次（已暂停计划亦可），不改变启停状态和下次自动执行时间。每次检查当前权限和凭据，同一计划不重叠、不补跑停机周期。</p>
      <el-button :disabled="busy" @click="changePlan(refreshPlans)">刷新完整配置计划</el-button>
      <div class="table-wrap"><table class="data-table"><thead><tr><th>固定设备范围</th><th>周期</th><th>状态</th><th>下次执行</th><th>最近结果</th><th>操作</th></tr></thead><tbody><tr v-for="plan in schedules" :key="plan.id"><td><details><summary>{{ plan.profileIds.length }} 台 · 查看档案</summary><ul><li v-for="profile in plan.profileIds" :key="profile"><span>{{ profile }}</span> <el-button text @click="emit('inspect', profile)">查看完整历史</el-button></li></ul></details></td><td>{{ plan.intervalMinutes / 60 }} 小时</td><td>{{ plan.enabled ? '已启用' : '已暂停' }}</td><td>{{ time(plan.nextRunMs) }}</td><td>{{ state(plan.lastState || 'NEVER_RUN') }} {{ failure(plan.lastError) }}<span v-if="plan.lastBatchId && plan.lastBatchAvailable === false" class="muted"> · 最近执行明细未保留，可查看各设备完整历史。</span><span v-if="plan.lastRunMs"> · 开始 {{ time(plan.lastRunMs) }}</span><span v-if="plan.lastCompletedMs"> · 完成 {{ time(plan.lastCompletedMs) }}</span></td><td><el-button text :disabled="busy || running || plan.lastState === 'RUNNING'" @click="runPlan(plan)">立即执行</el-button><el-button v-if="plan.lastBatchId" text :disabled="busy || running || plan.lastBatchAvailable === false" @click="inspectPlan(plan)">查看最近执行</el-button><el-button text :disabled="busy" @click="changePlan(() => setConfigBackupScheduleEnabled(plan.id, !plan.enabled))">{{ plan.enabled ? '暂停' : '启用' }}</el-button><el-button text :disabled="busy" @click="changePlan(() => deleteConfigBackupSchedule(plan.id))">删除</el-button></td></tr><tr v-if="!schedules.length"><td colspan="6" class="muted">暂无完整配置计划</td></tr></tbody></table></div>
      <ConfigurationRestoreBulkReadiness :selected-profile-ids="selectedProfileIds" />
    </div>
  </section>
</template>
