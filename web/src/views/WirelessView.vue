<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import WirelessOperationalCapabilities from '../components/WirelessOperationalCapabilities.vue'
import { wirelessObservationsCsv } from '../api/wirelessExport'
import { wirelessHistoryCsv } from '../api/wirelessHistoryExport'
import {
  createWirelessTask,
  getWirelessTasks,
  getWirelessTask,
  retryWirelessTask,
  downloadWirelessTask,
  cancelH3cTaskPlan,
  getH3cTaskPlan,
  getWirelessAcDomains,
  getWirelessApGroup,
  getWirelessApGroups,
  getWirelessApResources,
  getWirelessCollectionManifest,
  getWirelessCollectionManifests,
  getWirelessIdentityLifecycle,
  getWirelessIdentityReview,
  getWirelessRuntimeState,
  type WirelessAcDomain,
  type WirelessApGroup,
  type WirelessApGroupDetail,
  type WirelessApResource,
  type WirelessCollectionManifest,
  type WirelessCollectionManifestDetail,
  type WirelessIdentityLifecycle,
  type WirelessIdentityReview,
  type WirelessRuntimeState,
  type WirelessTaskPlan,
  type H3cTaskPlan,
  type WirelessTaskSummary,
  type WirelessTaskReport,
} from '../api/client'

const loading = ref(false)
const taskDomains = ref<string[]>([])
const taskAcs = ref<string[]>([])
const taskMembers = computed(() => domains.value.filter(domain => taskDomains.value.includes(domain.id)).flatMap(domain => domain.members).filter((member, index, all) => all.findIndex(item => item.deviceId === member.deviceId) === index))
watch(taskDomains, () => { taskAcs.value = taskAcs.value.filter(id => taskMembers.value.some(member => member.deviceId === id)) })
const taskHistory = ref<WirelessTaskSummary[]>([])
const historySite = ref('')
const historyAc = ref('')
const historyState = ref('')
const historySites = computed(() => [...new Set(taskHistory.value.flatMap(task => task.siteIds || []))].sort())
const historyAcs = computed(() => [...new Set(taskHistory.value.flatMap(task => task.acDeviceIds || []))].sort())
const historyStates = computed(() => [...new Set(['QUEUED', 'RUNNING', 'SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED', 'TIMED_OUT', 'INTERRUPTED', ...taskHistory.value.map(task => task.state)])])
const filteredTaskHistory = computed(() => taskHistory.value.filter(task =>
  (!historySite.value || task.siteIds?.includes(historySite.value)) &&
  (!historyAc.value || task.acDeviceIds?.includes(historyAc.value)) &&
  (!historyState.value || task.state === historyState.value)))
const taskDetail = ref<WirelessTaskReport | null>(null)
const taskRetryBusy = ref(false)
const taskRetryError = ref('')
const taskRetryRunning = computed(() => taskDetail.value?.retries?.some(retry => ['QUEUED', 'RUNNING'].includes(retry.state)) || false)
const taskCommandOptions = ['display version', 'display wlan ap all', 'display wlan ap all address', 'display wlan ap all radio', 'display wlan ac-role']
const taskCommands = ref(['display version', 'display wlan ap all', 'display wlan ap all address'])
const taskSubmitting = ref(false)
const taskError = ref('')
const taskResult = ref<WirelessTaskPlan | null>(null)
const taskPlan = ref<H3cTaskPlan | null>(null)
const domains = ref<WirelessAcDomain[]>([])
const resources = ref<WirelessApResource[]>([])
const groups = ref<WirelessApGroup[]>([])
const selectedDomainId = ref('')
const selectedAcId = ref('')
const selectedResourceId = ref('')
const search = ref('')
const error = ref('')
const partialError = ref('')
const groupError = ref('')
const groupDetail = ref<WirelessApGroupDetail | null>(null)
const groupDetailLoading = ref(false)
const groupDetailError = ref('')
const collectionManifests = ref<WirelessCollectionManifest[]>([])
const collectionError = ref('')
const collectionDetail = ref<WirelessCollectionManifestDetail | null>(null)
const collectionDetailLoading = ref(false)
const collectionDetailError = ref('')
const warnings = ref<string[]>([])
const page = ref(1)
const pageSize = 20
const stateFilter = ref('')
const runtimeState = ref<WirelessRuntimeState | null>(null)
const runtimeLoading = ref(false)
const runtimeError = ref('')
const identityReview = ref<WirelessIdentityReview | null>(null)
const identityLifecycle = ref<WirelessIdentityLifecycle | null>(null)
const identityLoading = ref(false)
const identityError = ref('')
let runtimeGeneration = 0
let runtimeController: AbortController | null = null
let identityGeneration = 0
let refreshGeneration = 0
let groupGeneration = 0
let groupDetailGeneration = 0
let collectionGeneration = 0
let collectionDetailGeneration = 0
let taskDetailGeneration = 0
let taskPoll: ReturnType<typeof setTimeout> | undefined
let refreshedTaskEvidence = ''
let activatedOnce = false

watch([historySite, historyAc, historyState], () => {
  ++taskDetailGeneration; clearTimeout(taskPoll); taskRetryError.value = ''
  if (taskDetail.value && filteredTaskHistory.value.some(task => task.id === taskDetail.value?.summary.id)) scheduleTaskPoll(taskDetail.value)
  else taskDetail.value = null
})

const selectedDomain = computed(() => domains.value.find(domain => domain.id === selectedDomainId.value) || domains.value[0] || null)
const visibleResources = computed(() => resources.value.filter(resource => {
  const inDomain = !selectedDomain.value || resource.sourceAcDomainIds.includes(selectedDomain.value.id)
  const inAc = !selectedAcId.value || resource.observations.some(observation => observation.sourceAcDeviceId === selectedAcId.value)
  const query = search.value.trim().toLowerCase()
  const matches = !query || resource.displayName.toLowerCase().includes(query) || resource.id.toLowerCase().includes(query) || (resource.serialEvidence || '').toLowerCase().includes(query) || resource.normalizedMacSet.some(mac => mac.toLowerCase().includes(query))
  const stateMatches = !stateFilter.value || viewObservations(resource).some(item => item.normalizedState === stateFilter.value)
  return inDomain && inAc && matches && stateMatches
}))
const pagedResources = computed(() => visibleResources.value.slice((page.value - 1) * pageSize, page.value * pageSize))
const selectedResource = computed(() => visibleResources.value.find(resource => resource.id === selectedResourceId.value) || pagedResources.value[0] || null)
const visibleGroups = computed(() => groups.value.filter(group => !selectedAcId.value || group.sourceAcDeviceIds.includes(selectedAcId.value)))
function groupSource(group: WirelessApGroup): string {
  if (group.groupScope === 'DOMAIN') return `Domain 来源 · ${group.sourceAcDomainId}`
  if (group.groupScope === 'AC') return `AC 来源 · ${group.sourceAcDeviceIds.join(' / ') || '待确认'}`
  return '来源待确认'
}
function groupReason(reason: string): string {
  return ({
    GROUP_SOURCE_KEY_OR_EVIDENCE_MISSING: '缺少可信的来源组键或采集证据',
    DOMAIN_SHARED_EVIDENCE_CONFLICT: '共享组证据与当前 AC Domain 成员不一致',
    DOMAIN_SHARED_EVIDENCE_PENDING: 'AC Domain 共享组证据尚待确认',
    GROUP_NAME_KEY_CONFLICT: '同一来源的同名组对应不同组键',
    GROUP_DISPLAY_NAME_CONFLICT: '同一组键存在不同名称，请核对来源',
    LEGACY_GROUP_NAME_ONLY: '历史记录仅有组名，来源身份尚待确认',
  } as Record<string, string>)[reason] || reason
}
function viewObservations(resource: WirelessApResource) {
  return resource.observations.filter(item => (!selectedDomain.value || item.sourceAcDomainId === selectedDomain.value.id) && (!selectedAcId.value || item.sourceAcDeviceId === selectedAcId.value))
}
function exportObservations(): void {
  const csv = wirelessObservationsCsv(visibleResources.value, {
    domainId: selectedDomain.value?.id || '', acDeviceId: selectedAcId.value,
    search: search.value.trim(), stateFilter: stateFilter.value, exportedMs: Date.now(), partial: !!partialError.value,
  })
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'wireless-observations.csv'
  link.click()
  URL.revokeObjectURL(url)
}
function exportRuntimeHistory(): void {
  if (runtimeLoading.value || runtimeError.value || !runtimeState.value || !selectedResource.value ||
      runtimeState.value.resourceId !== selectedResource.value.id) return
  const csv = wirelessHistoryCsv(selectedResource.value, runtimeState.value, {
    domainId: selectedDomain.value?.id || '', acDeviceId: selectedAcId.value, exportedMs: Date.now(),
  })
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url; link.download = 'wireless-ap-state-history.csv'; link.click(); URL.revokeObjectURL(url)
}
watch([search, stateFilter, selectedDomainId, selectedAcId], () => { page.value = 1; selectedResourceId.value = '' })
watch([selectedDomainId, selectedAcId], () => {
  groupDetail.value = null; groupDetailError.value = ''; ++groupDetailGeneration
  collectionDetail.value = null; collectionDetailError.value = ''; ++collectionDetailGeneration
})
watch([() => selectedResource.value?.id, () => selectedResource.value?.projectionRevision, selectedDomainId, selectedAcId], () => { void loadRuntime() })
watch([() => selectedResource.value?.id, () => selectedResource.value?.projectionRevision, selectedDomainId, selectedAcId], () => { void loadIdentityState() })

async function loadRuntime(): Promise<void> {
  const generation = ++runtimeGeneration
  runtimeController?.abort()
  runtimeState.value = null
  runtimeError.value = ''
  const resource = selectedResource.value
  if (!resource) { runtimeLoading.value = false; return }
  runtimeController = new AbortController()
  runtimeLoading.value = true
  try {
    const result = await getWirelessRuntimeState(resource.id, {
      sourceAcId: selectedAcId.value || undefined, domainId: selectedDomain.value?.id,
    }, runtimeController.signal)
    if (generation === runtimeGeneration) runtimeState.value = result
  } catch (cause) {
    if (generation === runtimeGeneration) runtimeError.value = cause instanceof Error ? cause.message : '状态历史读取失败'
  } finally { if (generation === runtimeGeneration) runtimeLoading.value = false }
}
async function loadIdentityState(): Promise<void> {
  const generation = ++identityGeneration
  identityReview.value = null
  identityLifecycle.value = null
  identityLoading.value = false
  identityError.value = ''
  const resource = selectedResource.value
  if (!resource) { identityLoading.value = false; return }
  identityLoading.value = true
  const [reviewResult, lifecycleResult] = await Promise.allSettled([
    getWirelessIdentityReview(resource.id),
    getWirelessIdentityLifecycle(resource.id),
  ])
  if (generation !== identityGeneration) return
  const failures: string[] = []
  if (reviewResult.status === 'fulfilled') identityReview.value = reviewResult.value
  else failures.push('身份核验')
  if (lifecycleResult.status === 'fulfilled') identityLifecycle.value = lifecycleResult.value
  else failures.push('生命周期')
  if (failures.length) identityError.value = `${failures.join('、')}读取失败，当前不显示未经核验的身份结论。`
  identityLoading.value = false
}
onBeforeUnmount(() => { ++runtimeGeneration; ++identityGeneration; runtimeController?.abort(); ++taskDetailGeneration; clearTimeout(taskPoll) })
const domainApCount = computed(() => visibleResources.value.length)
const scopeLabel = computed(() => selectedDomain.value?.siteScopeStatus || '—')
const runtimeLabel = computed(() => {
  return runtimeState.value?.normalizedState || 'UNKNOWN'
})

function selectDomain(domain: WirelessAcDomain): void {
  selectedDomainId.value = domain.id
  selectedAcId.value = ''
  selectedResourceId.value = ''
  void loadGroups(domain.id)
  void loadCollectionManifests(domain.id)
}
async function createTask(): Promise<void> {
  await submitTask(taskDomains.value, taskCommands.value, taskAcs.value.length ? taskAcs.value : undefined)
}
async function collectCurrentAc(): Promise<void> {
  const domain = selectedDomain.value
  if (!domain || !selectedAcId.value || taskSubmitting.value) return
  await submitTask([domain.id], ['display version', 'display wlan ap all', 'display wlan ap all address'], [selectedAcId.value])
}
async function submitTask(domainIds: string[], commands: string[], acIds?: string[]): Promise<void> {
  if (!domainIds.length || !commands.length || taskSubmitting.value) return
  taskSubmitting.value = true
  taskError.value = ''
  taskResult.value = null
  taskPlan.value = null
  try {
    taskResult.value = await createWirelessTask(domainIds, commands, acIds)
    taskPlan.value = taskResult.value.plan
    await loadTaskHistory()
    await showTask(taskResult.value.id)
  } catch (cause) {
    taskError.value = cause instanceof Error ? cause.message : '无线采集任务创建失败'
  } finally { taskSubmitting.value = false }
}
async function loadTaskHistory(): Promise<void> {
  try { taskHistory.value = await getWirelessTasks(); taskError.value = '' }
  catch (cause) { ++taskDetailGeneration; clearTimeout(taskPoll); taskHistory.value = []; taskDetail.value = null; taskError.value = cause instanceof Error ? cause.message : '任务历史读取失败' }
}
async function showTask(id: string): Promise<void> {
  if (taskRetryBusy.value) return
  const generation = ++taskDetailGeneration
  clearTimeout(taskPoll)
  if (taskDetail.value?.summary.id !== id) { taskDetail.value = null; taskRetryError.value = '' }
  try {
    const detail = await getWirelessTask(id)
    if (generation !== taskDetailGeneration) return
    taskDetail.value = detail; taskError.value = ''
    scheduleTaskPoll(detail)
    await refreshFinishedTaskEvidence(detail)
  } catch (cause) { if (generation === taskDetailGeneration) taskError.value = cause instanceof Error ? cause.message : '任务详情读取失败' }
}
async function refreshFinishedTaskEvidence(detail: WirelessTaskReport): Promise<void> {
  if (['QUEUED', 'RUNNING'].includes(detail.summary.state) || detail.retries?.some(retry => ['QUEUED', 'RUNNING'].includes(retry.state))) return
  const revision = `${detail.summary.id}:${detail.summary.updatedMs}:${(detail.retries || []).map(retry => `${retry.retryRunId}:${retry.state}`).join('|')}`
  if (revision === refreshedTaskEvidence) return
  refreshedTaskEvidence = revision
  await refresh()
}
function scheduleTaskPoll(detail: WirelessTaskReport): void {
  if (['QUEUED', 'RUNNING'].includes(detail.summary.state) || detail.retries?.some(retry => ['QUEUED', 'RUNNING'].includes(retry.state)))
    taskPoll = setTimeout(() => { void showTask(detail.summary.id) }, 3000)
}
function canRetryItem(item: WirelessTaskReport['items'][number]): boolean {
  return ['FAILED', 'TIMED_OUT', 'CANCELLED', 'INTERRUPTED'].includes(item.state) && Boolean(item.runId) && Boolean(item.commands?.some(command => command.state !== 'SUCCEEDED'))
}
async function retryAc(ordinal: number, commandIndexes?: number[]): Promise<void> {
  if (!taskDetail.value || taskRetryBusy.value || taskRetryRunning.value) return
  const id = taskDetail.value.summary.id
  const generation = ++taskDetailGeneration
  clearTimeout(taskPoll)
  taskRetryBusy.value = true; taskRetryError.value = ''
  try {
    const detail = await retryWirelessTask(id, ordinal, commandIndexes)
    if (generation !== taskDetailGeneration) return
    taskDetail.value = detail
    scheduleTaskPoll(detail)
    await refreshFinishedTaskEvidence(detail)
  } catch (cause) {
    if (generation === taskDetailGeneration) taskRetryError.value = cause instanceof Error ? cause.message : '重试提交失败'
  } finally { taskRetryBusy.value = false }
}
async function cancelTaskPlan(id: string): Promise<void> {
  try { await cancelH3cTaskPlan(id); await loadTaskHistory(); await showTask(id) }
  catch (cause) { taskError.value = cause instanceof Error ? cause.message : '取消任务失败' }
}
async function downloadTask(id: string): Promise<void> {
  try {
    const url = URL.createObjectURL(await downloadWirelessTask(id))
    const link = document.createElement('a'); link.href = url; link.download = `wireless-task-${id}.zip`; link.click(); URL.revokeObjectURL(url)
  } catch (cause) { taskError.value = cause instanceof Error ? cause.message : '任务下载失败' }
}
async function refreshTask(): Promise<void> {
  if (!taskResult.value) return
  try { taskPlan.value = await getH3cTaskPlan(taskResult.value.id); taskError.value = '' }
  catch (cause) { taskError.value = cause instanceof Error ? cause.message : '任务状态读取失败' }
}
function selectAc(domain: WirelessAcDomain, deviceId: string): void {
  selectedDomainId.value = domain.id
  selectedAcId.value = deviceId
  selectedResourceId.value = ''
  void loadGroups(domain.id)
  void loadCollectionManifests(domain.id)
}
async function readAll<T>(read: (params: { page: number; pageSize: number }) => Promise<{ items: T[]; hasMore: boolean; warnings?: string[]; sourceRevision?: string }>): Promise<T[]> {
  const items: T[] = []
  let revision: string | undefined
  for (let current = 1; ; current++) {
    const result = await read({ page: current, pageSize: 100 })
    if (current === 1) revision = result.sourceRevision
    else if (revision !== result.sourceRevision) throw new Error('采集数据已变化，请刷新重试')
    items.push(...result.items)
    warnings.value = [...new Set([...warnings.value, ...(result.warnings || [])])]
    if (!result.hasMore) return items
    if (!result.items.length) throw new Error('分页响应不完整，请刷新重试')
  }
}
async function loadGroups(domainId: string): Promise<void> {
  const generation = ++groupGeneration
  groups.value = []
  groupError.value = ''
  groupDetail.value = null
  groupDetailError.value = ''
  try {
    const result = await readAll(params => getWirelessApGroups(domainId, { ...params, sourceAcId: selectedAcId.value || undefined }))
    if (generation === groupGeneration) groups.value = result
  } catch (cause) {
    if (generation === groupGeneration) groupError.value = cause instanceof Error ? cause.message : 'AP 组读取失败'
  }
}
async function loadCollectionManifests(domainId: string): Promise<void> {
  const generation = ++collectionGeneration
  collectionManifests.value = []
  collectionError.value = ''
  collectionDetail.value = null
  collectionDetailError.value = ''
  try {
    const result = await readAll(params => getWirelessCollectionManifests(domainId, { ...params, sourceAcId: selectedAcId.value || undefined }))
    if (generation === collectionGeneration) collectionManifests.value = result
  } catch (cause) {
    if (generation === collectionGeneration) collectionError.value = cause instanceof Error ? cause.message : '采集记录读取失败'
  }
}
async function selectCollectionManifest(manifest: WirelessCollectionManifest): Promise<void> {
  const generation = ++collectionDetailGeneration
  collectionDetail.value = null
  collectionDetailError.value = ''
  collectionDetailLoading.value = true
  try {
    const result = await getWirelessCollectionManifest(manifest.id, { sourceAcId: selectedAcId.value || undefined })
    if (generation === collectionDetailGeneration) collectionDetail.value = result
  } catch (cause) {
    if (generation === collectionDetailGeneration) collectionDetailError.value = cause instanceof Error ? cause.message : '采集记录详情读取失败'
  } finally {
    if (generation === collectionDetailGeneration) collectionDetailLoading.value = false
  }
}
async function selectGroup(group: WirelessApGroup): Promise<void> {
  const generation = ++groupDetailGeneration
  groupDetail.value = null
  groupDetailError.value = ''
  groupDetailLoading.value = true
  try {
    const result = await getWirelessApGroup(group.id, { sourceAcId: selectedAcId.value || undefined })
    if (generation === groupDetailGeneration) groupDetail.value = result
  } catch (cause) {
    if (generation === groupDetailGeneration) groupDetailError.value = cause instanceof Error ? cause.message : 'AP 组详情读取失败'
  } finally {
    if (generation === groupDetailGeneration) groupDetailLoading.value = false
  }
}
async function refresh(): Promise<void> {
  const generation = ++refreshGeneration
  ++groupGeneration
  ++collectionGeneration
  ++runtimeGeneration
  runtimeController?.abort()
  runtimeState.value = null
  ++identityGeneration
  identityReview.value = null
  identityLifecycle.value = null
  identityLoading.value = false
  identityError.value = ''
  loading.value = true
  error.value = ''
  groupError.value = ''
  collectionError.value = ''
  groupDetail.value = null
  groupDetailError.value = ''
  ++groupDetailGeneration
  collectionManifests.value = []
  collectionDetail.value = null
  collectionDetailError.value = ''
  ++collectionDetailGeneration
  warnings.value = []
  partialError.value = ''
  domains.value = []
  resources.value = []
  try {
    const [domainResult, resourceResult] = await Promise.allSettled([
      readAll(params => getWirelessAcDomains(params)),
      readAll(params => getWirelessApResources(params)),
    ])
    if (generation !== refreshGeneration) return
    const failed: string[] = []
    if (domainResult.status === 'fulfilled') domains.value = domainResult.value
    else failed.push('AC Domain')
    if (resourceResult.status === 'fulfilled') resources.value = resourceResult.value
    else failed.push('AP 资源')
    if (failed.length === 2) throw new Error('AC Domain 和 AP 资源均读取失败')
    taskDomains.value = taskDomains.value.filter(id => domains.value.some(domain => domain.id === id))
    if (failed.length) {
      const details = [domainResult, resourceResult]
        .filter(result => result.status === 'rejected')
        .map(result => result.reason instanceof Error ? result.reason.message : '服务端返回错误')
      partialError.value = `${failed.join('、')}读取失败，已保留其余可用数据。${details.join(' ')}`
    }
    if (!domains.value.some(domain => domain.id === selectedDomainId.value)) selectedDomainId.value = domains.value[0]?.id || ''
    if (!selectedDomain.value?.members.some(member => member.deviceId === selectedAcId.value)) selectedAcId.value = ''
    page.value = 1
    selectedResourceId.value = ''
    if (selectedDomainId.value) await Promise.all([loadGroups(selectedDomainId.value), loadCollectionManifests(selectedDomainId.value)])
    else { groups.value = []; collectionManifests.value = [] }
    await loadRuntime()
  } catch (cause) {
    if (generation !== refreshGeneration) return
    domains.value = []; resources.value = []; groups.value = []; collectionManifests.value = []
    selectedDomainId.value = ''; selectedAcId.value = ''; selectedResourceId.value = ''
    partialError.value = ''
    error.value = cause instanceof Error ? cause.message : '无线业务数据读取失败'
    ElMessage.error(error.value)
  } finally { if (generation === refreshGeneration) loading.value = false }
}
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string {
  return ['RUNNING_MASTER', 'RUNNING_BACKUP', 'CONFIRMED', 'ACCEPTED'].includes(state) ? 'good' : ['UNKNOWN', 'STALE', 'CANDIDATE', 'EVIDENCE_PENDING', 'PENDING_REVIEW'].includes(state) ? 'warn' : ['OFFLINE', 'CONFLICT', 'REJECTED', 'SUPERSEDED'].includes(state) ? 'bad' : 'neutral'
}
function stateText(state: string): string {
  return ({ RUNNING_MASTER: '运行主', RUNNING_BACKUP: '运行备', OFFLINE: '离线', UNKNOWN: '未知', STALE: '过期', CONFLICT: '冲突', CANDIDATE: '候选', CONFIRMED: '已确认', EVIDENCE_PENDING: '证据待定', PENDING_REVIEW: '待核验', ACCEPTED: '已接受', REJECTED: '已拒绝', SUPERSEDED: '已替换' } as Record<string, string>)[state] || state
}
function outcomeText(outcome: string): string {
  return ({ SUCCESS: '有效采集', FAILED: '采集失败', EMPTY: '空表', PARTIAL: '采集不完整', UNSUPPORTED: '命令不支持',
    PARSE_FAILED: '解析失败', UNKNOWN: '未知状态', CONFLICT: '来源冲突', CLOCK_SKEW: '采集时间异常', NOT_COLLECTED: '未采集' } as Record<string, string>)[outcome] || outcome
}

onMounted(() => { void refresh(); void loadTaskHistory() })
onActivated(() => {
  // KeepAlive activates on first mount too. Reload all evidence on later visits.
  if (activatedOnce) { void refresh(); void loadTaskHistory() }
  activatedOnce = true
})
</script>

<template>
  <NcLayout title="无线业务" description="按 AC Domain 查看 FIT-AP 物理身份、来源 Observation、AP 组与主备运行证据">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新数据</el-button></template>

    <el-alert v-if="warnings.includes('OFFLINE_FIXTURE_EVIDENCE')" title="离线演示数据 · 非模拟器实时状态" type="warning" :closable="false" show-icon />
    <el-alert v-if="partialError" :title="partialError" type="warning" :closable="false" show-icon />
    <div class="wireless-layout" v-loading="loading">
      <aside class="card wireless-sidebar">
        <div class="card__header"><h2>AC Domain</h2><span class="state-pill info">{{ domains.length }} 个</span></div>
        <div v-if="domains.length" class="wireless-tree">
          <div v-for="domain in domains" :key="domain.id" class="wireless-tree__domain">
            <button :class="['wireless-tree__item', { active: selectedDomain?.id === domain.id && !selectedAcId }]" type="button" @click="selectDomain(domain)">
              <span><strong>{{ domain.displayName }}</strong><small>{{ domain.domainKind }} · {{ stateText(domain.topologyStatus) }}</small></span>
              <span class="state-pill" :class="stateClass(domain.topologyStatus)">{{ domain.memberDeviceIds.length }}</span>
            </button>
            <button v-for="member in domain.members" :key="member.deviceId" :class="['wireless-tree__member', { active: selectedAcId === member.deviceId }]" type="button" @click="selectAc(domain, member.deviceId)">
              <span><span class="tree-branch">└</span>{{ member.displayName || member.deviceId }}</span><span class="mono">{{ member.deviceId.slice(0, 8) }}</span>
            </button>
          </div>
        </div>
        <div v-else class="empty-state"><div><strong>暂无可见 AC Domain</strong><small>{{ error || '需要服务端 Site claims 与 AC inventory。' }}</small></div></div>
      </aside>

      <main class="wireless-main">
        <section v-if="domains.length" class="card wireless-task">
          <div class="card__header"><h2>无线采集任务</h2></div>
          <div v-if="selectedAcId" class="wireless-task-quick"><span>当前 AC：{{ selectedDomain?.members.find(member => member.deviceId === selectedAcId)?.displayName || selectedAcId }}</span><el-button class="wireless-task-quick-collect" size="small" :loading="taskSubmitting" :disabled="taskSubmitting" @click="collectCurrentAc">采集当前 AC</el-button></div>
          <div><label v-for="domain in domains" :key="domain.id" style="display:inline-flex;gap:6px;margin:0 16px 8px 0"><input v-model="taskDomains" type="checkbox" :value="domain.id">{{ domain.displayName }}</label></div>
          <div v-if="taskMembers.length"><span>AC（不选则采集全部成员）：</span><label v-for="member in taskMembers" :key="member.deviceId" style="display:inline-flex;gap:6px;margin:0 16px 8px 0"><input v-model="taskAcs" type="checkbox" :value="member.deviceId">{{ member.displayName }}</label></div>
          <div><label v-for="command in taskCommandOptions" :key="command" style="display:inline-flex;gap:6px;margin:0 16px 8px 0"><input v-model="taskCommands" type="checkbox" :value="command">{{ command }}</label></div>
          <el-button size="small" :loading="taskSubmitting" :disabled="!taskDomains.length || !taskCommands.length || taskSubmitting" @click="createTask">开始只读采集</el-button>
          <p v-if="taskError" role="alert">{{ taskError }}</p>
          <div v-if="taskResult && taskPlan" class="wireless-task-result">
            <p>任务 {{ taskResult.id }} · {{ taskPlan.state }} · {{ taskPlan.items.length }} 台 AC</p>
            <p v-for="item in taskPlan.items" :key="item.ordinal">{{ item.target }} · {{ item.state }} <span v-if="item.error">· {{ item.error }}</span></p>
            <el-button size="small" @click="refreshTask">刷新任务状态</el-button>
          </div>
        </section>
        <section class="card wireless-task-history">
          <div class="card__header"><h2>无线任务历史</h2><el-button size="small" @click="loadTaskHistory">刷新</el-button></div>
          <p v-if="taskError && !domains.length" role="alert">{{ taskError }}</p>
          <div class="toolbar wireless-history-filters">
            <label>Site <select v-model="historySite" aria-label="任务历史 Site"><option value="">全部 Site</option><option v-for="site in historySites" :key="site" :value="site">{{ site }}</option></select></label>
            <label>AC <select v-model="historyAc" aria-label="任务历史 AC"><option value="">全部 AC</option><option v-for="ac in historyAcs" :key="ac" :value="ac">{{ ac }}</option></select></label>
            <label>状态 <select v-model="historyState" aria-label="任务历史状态"><option value="">全部状态</option><option v-for="state in historyStates" :key="state" :value="state">{{ state }}</option></select></label>
            <el-button size="small" @click="historySite = ''; historyAc = ''; historyState = ''">清除筛选</el-button>
          </div>
          <p class="muted wireless-history-count">显示 {{ filteredTaskHistory.length }} / {{ taskHistory.length }} 条（仅筛选最近最多 200 条可见任务）</p>
          <div v-if="!taskHistory.length" class="empty-state">暂无可见任务</div>
          <div v-else-if="!filteredTaskHistory.length" class="empty-state">没有符合筛选条件的任务</div>
          <div v-for="task in filteredTaskHistory" :key="task.id" class="wireless-task-row" :data-task-id="task.id"><button type="button" @click="showTask(task.id)">{{ formatTime(task.createdMs) }} · {{ task.state }} · {{ task.succeeded }}/{{ task.total }} 成功 · {{ task.failed }} 失败</button></div>
          <div v-if="taskDetail" class="wireless-task-detail">
            <h3>任务详情 · {{ taskDetail.summary.state }}</h3>
            <p v-if="taskRetryError" role="alert">{{ taskRetryError }}</p>
            <el-button size="small" :disabled="taskRetryBusy" @click="showTask(taskDetail.summary.id)">刷新详情</el-button>
            <p>任务 {{ taskDetail.summary.id }} · 创建 {{ formatTime(taskDetail.summary.createdMs) }} · 更新 {{ formatTime(taskDetail.summary.updatedMs) }} · 成功 {{ taskDetail.summary.succeeded }} / {{ taskDetail.summary.total }} · 失败 {{ taskDetail.summary.failed }}</p>
            <div v-for="item in taskDetail.items" :key="item.ordinal" class="wireless-task-ac">
              <p>{{ item.deviceId }} · {{ item.state }} · 采集 {{ item.captureStatus || '—' }} · 解析 {{ item.parseStatus || '—' }} <span v-if="item.error">· {{ item.error }}</span></p>
              <el-button v-if="canRetryItem(item)" size="small" :disabled="taskRetryBusy || taskRetryRunning" @click="retryAc(item.ordinal)">重试 {{ item.deviceId }} 的失败命令</el-button>
              <p v-for="(command, index) in item.commands" :key="command.command" class="muted">{{ command.command }} · {{ command.state }} · 采集 {{ command.captureStatus || '—' }} · 解析 {{ command.parseStatus || '—' }} <span v-if="command.error">· {{ command.error }}</span>
                <el-button v-if="canRetryItem(item) && command.state !== 'SUCCEEDED'" size="small" :disabled="taskRetryBusy || taskRetryRunning" @click="retryAc(item.ordinal, [index])">重试此命令</el-button>
              </p>
            </div>
            <section class="wireless-retry-history">
              <h4>重试历史 · {{ taskDetail.retries?.length || 0 }} 次</h4>
              <p v-if="!taskDetail.retries?.length">暂无重试记录</p>
              <div v-for="retry in taskDetail.retries || []" :key="retry.retryRunId">
                <p>{{ retry.deviceId }} · {{ retry.state }} · {{ formatTime(retry.createdMs) }} · 原任务 {{ retry.originalRunId }} · 重试 {{ retry.retryRunId }}</p>
                <p v-for="command in retry.commands" :key="command.command">{{ command.command }} · {{ command.state }} <span v-if="command.error">· {{ command.error }}</span></p>
              </div>
            </section>
            <el-button size="small" @click="downloadTask(taskDetail.summary.id)">下载 ZIP</el-button>
            <el-button v-if="['QUEUED', 'RUNNING'].includes(taskDetail.summary.state)" size="small" @click="cancelTaskPlan(taskDetail.summary.id)">取消任务</el-button>
          </div>
        </section>
        <div v-if="error && !domains.length" class="card empty-state"><div><strong>无线业务暂不可用</strong><small>{{ error }}</small></div></div>
        <template v-else-if="selectedDomain">
          <section class="card wireless-domain-header">
            <div><span class="page-eyebrow">无线管理域</span><h2>{{ selectedDomain.displayName }}</h2><p class="wireless-subtitle">{{ selectedAcId ? 'AC 视图 · ' + (selectedDomain.members.find(member => member.deviceId === selectedAcId)?.displayName || selectedAcId) : 'Domain 视图 · 物理 AP 身份不因来源 AC 重复生成' }}</p></div>
            <div class="wireless-domain-meta"><span :class="['state-pill', stateClass(selectedDomain.topologyStatus)]">{{ selectedDomain.domainKind }} / {{ stateText(selectedDomain.topologyStatus) }}</span><span class="state-pill info">Scope {{ scopeLabel }}</span></div>
          </section>

          <div class="stats-grid wireless-stats">
            <article class="card stat-card"><div class="stat-card__label">成员 AC</div><div class="stat-card__value">{{ selectedDomain.members.length }}</div><div class="stat-card__meta">设备库存稳定 ID</div></article>
            <article class="card stat-card"><div class="stat-card__label">可见 FIT-AP</div><div class="stat-card__value">{{ domainApCount }}</div><div class="stat-card__meta">授权 Observation 投影</div></article>
            <article class="card stat-card"><div class="stat-card__label">AP 观察状态</div><div class="stat-card__value stat-card__value--small">{{ stateText(runtimeLabel) }}</div><div class="stat-card__meta">仅依据可见运行证据</div></article>
            <article class="card stat-card"><div class="stat-card__label">最近采集</div><div class="stat-card__value stat-card__value--small">{{ formatTime(selectedDomain.asOf) }}</div><div class="stat-card__meta">{{ selectedDomain.revision.slice(0, 12) }}</div></article>
          </div>

          <section class="card wireless-members"><div class="card__header"><h2>AC 成员与 AP 观察状态</h2><span class="state-pill">1+1 需运行证据确认</span></div><div class="wireless-member-grid"><article v-for="member in selectedDomain.members" :key="member.deviceId" class="wireless-member-card"><div class="wireless-member-card__top"><strong>{{ member.displayName }}</strong><span :class="['state-pill', stateClass(member.runtimeState)]">{{ stateText(member.runtimeState) }}</span></div><div class="wireless-member-card__line"><span>Device ID</span><code>{{ member.deviceId }}</code></div><div class="wireless-member-card__line"><span>地址 / 版本</span><span>{{ member.managementIp }} · {{ member.version || '—' }}</span></div><div class="wireless-member-card__line"><span>来源状态</span><span>{{ member.connectionStatus }} · {{ member.role }}</span></div></article></div></section>

          <section class="card wireless-collection"><div class="card__header"><h2>采集记录</h2><span class="state-pill info">Raw provenance</span></div><div v-if="collectionManifests.length" class="wireless-collection-list"><button v-for="manifest in collectionManifests" :key="manifest.id" class="wireless-collection-row" :class="{ selected: collectionDetail?.manifest.id === manifest.id }" type="button" @click="selectCollectionManifest(manifest)"><span><strong>{{ manifest.sourceRevision }}</strong><small>{{ manifest.sourceAcDeviceIds.join(' / ') }} · {{ formatTime(manifest.lastCapturedMs) }}</small></span><span>{{ manifest.observationCount }} 条 Observation</span></button></div><div v-else class="empty-state"><div><strong>{{ collectionError ? '采集记录读取失败' : '暂无采集记录' }}</strong><small>{{ collectionError || '当前 AC 作用域没有可见的跨 AC collection manifest。' }}</small></div></div><div v-if="collectionDetailLoading" class="wireless-collection-detail muted-text">正在读取采集记录…</div><div v-else-if="collectionDetailError" class="wireless-collection-detail bad-text">{{ collectionDetailError }}</div><div v-else-if="collectionDetail" class="wireless-collection-detail"><div class="card__header"><h3>{{ collectionDetail.manifest.sourceRevision }}</h3><span class="state-pill info">{{ collectionDetail.manifest.status }}</span></div><div class="wireless-collection-detail__meta"><span>来源 AC <code>{{ collectionDetail.manifest.sourceAcDeviceIds.join(' / ') }}</code></span><span>Observation <code>{{ collectionDetail.items.length }}</code></span><span>Scope <code>{{ collectionDetail.manifest.scopeStatus }}</code></span></div><div class="wireless-collection-item" v-for="item in collectionDetail.items.slice(0, 20)" :key="item.observationId"><span><strong>{{ item.sourceAcDeviceId }}</strong><small>{{ item.sourceAcDomainId }} · {{ formatTime(item.capturedMs) }}</small></span><span><code>{{ item.rawManifestRef }}</code> · {{ item.outcome }}</span></div></div></section>

          <section class="card"><div class="card__header"><div><h2>FIT-AP</h2><p class="wireless-subtitle">FIT-AP 由当前主用 AC 管理；远程控制台需先确认主用 AC 和控制台状态，不在设备库存中单独新增。</p></div><div class="wireless-toolbar"><input v-model="search" aria-label="搜索 AP" placeholder="Serial / MAC / 名称搜索" /><select v-model="stateFilter" aria-label="筛选 AP 状态"><option value="">所有状态</option><option v-for="state in ['RUNNING_MASTER', 'RUNNING_BACKUP', 'OFFLINE', 'UNKNOWN', 'STALE', 'CONFLICT']" :key="state" :value="state">{{ stateText(state) }}</option></select><span class="state-pill info">{{ visibleResources.length }} 条</span><el-button size="small" :disabled="loading || !visibleResources.some(resource => viewObservations(resource).length)" @click="exportObservations">导出当前 AP 观察 CSV</el-button></div></div><div v-if="visibleResources.length" class="table-wrap"><table class="data-table wireless-table"><thead><tr><th>物理 AP</th><th>Serial</th><th>完整 MAC</th><th>来源 AC</th><th>AP 组</th><th>状态</th><th>身份</th><th>最近观察</th></tr></thead><tbody><tr v-for="resource in pagedResources" :key="resource.id" :class="{ selected: selectedResource?.id === resource.id }" @click="selectedResourceId = resource.id"><td><span class="device-name">{{ resource.displayName }}</span><span class="device-subline mono">{{ resource.id }}</span></td><td class="mono">{{ resource.serialEvidence || '—' }}</td><td class="mono">{{ resource.normalizedMacSet.join(' / ') || '—' }}</td><td><span v-for="observation in viewObservations(resource)" :key="observation.id" class="source-tag">{{ observation.sourceAcDeviceId }}</span></td><td>{{ viewObservations(resource).map(observation => observation.apGroup).filter(Boolean).join(' / ') || '—' }}</td><td><span v-for="observation in viewObservations(resource)" :key="observation.id" :class="['state-pill', stateClass(observation.normalizedState)]">{{ stateText(observation.normalizedState) }}</span></td><td><span :class="['state-pill', stateClass(resource.identityStatus)]">{{ stateText(resource.identityStatus) }}</span></td><td>{{ formatTime(resource.lastSeenMs) }}</td></tr></tbody></table></div><div v-else class="empty-state"><div><strong>暂无可见 AP Observation</strong><small>当前筛选下没有 AP，请调整搜索或状态筛选。</small></div></div></section>

          <el-pagination v-if="visibleResources.length > pageSize" v-model:current-page="page" :page-size="pageSize" :total="visibleResources.length" layout="total, prev, pager, next" />
          <div class="wireless-bottom-grid">
            <section class="card wireless-groups">
              <div class="card__header"><h2>AP 组</h2><span class="state-pill">按来源保留</span></div>
              <div v-if="visibleGroups.length" class="wireless-group-list">
                <button v-for="group in visibleGroups" :key="group.id" class="wireless-group-row" :class="{ selected: groupDetail?.group.id === group.id }" type="button" @click="selectGroup(group)">
                  <span><strong>{{ group.displayName }}</strong><small>{{ groupSource(group) }}</small><small class="mono">{{ group.groupIdentityKey || '稳定身份待确认' }}</small></span>
                  <span class="wireless-group-row__status"><span :class="['state-pill', stateClass(group.groupIdentityStatus)]">{{ stateText(group.groupIdentityStatus) }}</span><small>{{ group.memberResourceIds.length }} 个 AP</small></span>
                </button>
              </div>
              <div v-else class="empty-state"><div><strong>{{ groupError ? 'AP 组读取失败' : '暂无可见 AP 组' }}</strong><small>{{ groupError || '仅显示当前可见来源的 AP 组。' }}</small></div></div>
              <div v-if="groupDetailLoading" class="wireless-group-detail muted-text">正在读取组详情…</div>
              <div v-else-if="groupDetailError" class="wireless-group-detail bad-text">{{ groupDetailError }}</div>
              <div v-else-if="groupDetail" class="wireless-group-detail">
                <div class="card__header"><h3>{{ groupDetail.group.displayName }}</h3><span :class="['state-pill', stateClass(groupDetail.provenance.groupIdentityStatus)]">{{ stateText(groupDetail.provenance.groupIdentityStatus) }}</span></div>
                <p>{{ groupSource(groupDetail.group) }}</p>
                <p v-if="groupDetail.provenance.conflictReason" :class="groupDetail.provenance.groupIdentityStatus === 'CONFLICT' ? 'bad-text' : 'warn-text'">{{ groupReason(groupDetail.provenance.conflictReason) }}</p>
                <dl class="wireless-group-provenance">
                  <dt>稳定身份键</dt><dd><code>{{ groupDetail.provenance.groupIdentityKey || '待确认，尚无可信来源键' }}</code></dd>
                  <dt>投影 ID</dt><dd><code>{{ groupDetail.group.id }}</code></dd>
                  <dt>来源组键</dt><dd><code>{{ groupDetail.provenance.sourceGroupKey || '未提供' }}</code></dd>
                  <dt>来源 AC</dt><dd><code>{{ groupDetail.provenance.sourceAcDeviceIds.join(' / ') || '待确认' }}</code></dd>
                  <dt>来源 Domain</dt><dd><code>{{ groupDetail.provenance.sourceAcDomainId || '待确认' }}</code></dd>
                  <dt>来源版本</dt><dd><code>{{ groupDetail.provenance.sourceRevision }}</code></dd>
                  <dt>成员版本</dt><dd><code>{{ groupDetail.provenance.membershipRevision }}</code></dd>
                  <dt>来源观察</dt><dd><code>{{ groupDetail.provenance.sourceObservationIds.join(' / ') || '暂无可见观察' }}</code></dd>
                  <dt>证据引用</dt><dd><code>{{ groupDetail.provenance.sourceEvidenceRefs.join(' / ') || '暂无可见证据' }}</code></dd>
                  <dt>观察时间</dt><dd>{{ formatTime(groupDetail.provenance.observedAtMs) }}</dd>
                  <dt>查询时间 / 范围</dt><dd>{{ formatTime(groupDetail.asOf) }} · {{ groupDetail.provenance.scopeStatus }}</dd>
                </dl>
                <small>可见成员 {{ groupDetail.total }} 个；成员与来源证据经过当前 Site claims 过滤。</small>
              </div>
            </section>
            <section class="card"><div class="card__header"><h2>AP 来源状态</h2><span class="state-pill info">{{ selectedResource ? viewObservations(selectedResource).length : 0 }} 条来源</span></div><el-alert v-if="selectedResource?.stateConflictReason" title="来源状态存在冲突，请核对各 AC 的原始观察" type="warning" :closable="false" /><div v-if="selectedResource" class="wireless-observation-list"><article v-for="observation in viewObservations(selectedResource)" :key="observation.id" class="wireless-observation"><div><strong>{{ observation.sourceAcDeviceId }}</strong><span class="state-pill" :class="stateClass(observation.normalizedState)">{{ stateText(observation.normalizedState) }}</span></div><small>最近采集 {{ formatTime(observation.capturedMs) }}</small><details class="wireless-observation__evidence"><summary>采集证据</summary><dl><dt>来源站点</dt><dd>{{ observation.sourceSiteId }}</dd><dt>设备原始状态</dt><dd>{{ observation.rawState }}</dd><dt>状态关联</dt><dd>{{ observation.relationStatus }}</dd><dt>解析版本</dt><dd>{{ observation.parserRevision }}</dd><dt>事实引用</dt><dd><code>{{ observation.rawManifestRef }}</code></dd></dl></details></article></div><div v-else class="empty-state"><div><strong>选择一条 AP</strong><small>查看每台 AC 独立采集到的运行状态。</small></div></div></section>
          </div>
          <section v-if="selectedResource" class="card wireless-identity" v-loading="identityLoading">
            <div class="card__header"><h2>AP 身份核验</h2><span class="state-pill" :class="stateClass(identityLifecycle?.state || identityReview?.identityStatus || selectedResource.identityStatus)">{{ stateText(identityLifecycle?.state || identityReview?.identityStatus || selectedResource.identityStatus) }}</span></div>
            <el-alert v-if="identityError" :title="identityError" type="warning" :closable="false" show-icon />
            <template v-else-if="identityReview">
              <div class="wireless-state-values wireless-identity-values">
                <div><small>当前身份</small><strong>{{ stateText(identityReview.identityStatus) }}</strong><span>{{ identityReview.scopeStatus }}</span></div>
                <div><small>生命周期</small><strong>{{ stateText(identityLifecycle?.state || 'PENDING_REVIEW') }}</strong><span>{{ identityLifecycle?.actor || '尚无决策' }}</span></div>
                <div><small>精确证据</small><strong>{{ identityReview.evidenceRefs.length }} 条</strong><span>{{ identityReview.canConfirm ? '满足确认前置条件' : '仍需人工核验' }}</span></div>
              </div>
              <dl class="wireless-identity-provenance">
                <dt>核验修订</dt><dd><code>{{ identityReview.identityRevision }}</code></dd>
                <dt>冲突原因</dt><dd>{{ identityReview.conflictReason || '无' }}</dd>
                <dt>阻断原因</dt><dd>{{ identityReview.decisionBlockReason || '无' }}</dd>
                <dt>证据引用</dt><dd><code>{{ identityReview.evidenceRefs.join(' / ') || '暂无可见证据' }}</code></dd>
                <dt>生命周期修订</dt><dd><code>{{ identityLifecycle?.revision || '尚无持久决策' }}</code></dd>
              </dl>
              <p class="wireless-runtime-note">此处只展示当前 Site claims 可见的精确 Serial/MAC、Observation 和 Raw 引用；不会因缺少证据自动确认、替换或推断离线。</p>
            </template>
          </section>
          <section v-if="selectedResource" class="card wireless-runtime" v-loading="runtimeLoading">
            <div class="card__header"><h2>状态与最近变化</h2><div class="wireless-toolbar"><span class="state-pill" :class="stateClass(runtimeState?.normalizedState || 'UNKNOWN')">{{ stateText(runtimeState?.normalizedState || 'UNKNOWN') }}</span><el-button size="small" :disabled="runtimeLoading || !!runtimeError || !runtimeState || runtimeState.resourceId !== selectedResource.id" @click="exportRuntimeHistory">导出 AP 状态历史 CSV</el-button></div></div>
            <el-alert v-if="runtimeError" :title="'状态历史读取失败：' + runtimeError" type="error" :closable="false" show-icon />
            <template v-else-if="runtimeState">
              <p class="wireless-runtime-note">仅显示当前可见 AC 的有效状态变化；无变化采集不增加记录。时效窗口：{{ runtimeState.freshnessWindowMs / 1000 }} 秒。导出仅含已加载的当前、上一有效状态及最近十次变化，不代表完整历史或 HA 切换。</p>
              <el-alert v-if="runtimeState.warnings.includes('OFFLINE_FIXTURE_EVIDENCE')" title="以下为离线状态历史，不能证明设备当前在线" type="warning" :closable="false" />
              <div class="wireless-state-values wireless-ap-state-values">
                <div><small>AP 最近有效状态</small><strong>{{ runtimeState.current ? stateText(runtimeState.current.normalizedState) : '暂无有效证据' }}</strong><span>{{ formatTime(runtimeState.current?.capturedMs) }}</span></div>
                <div><small>AP 上一有效状态</small><strong>{{ runtimeState.previousEffective ? stateText(runtimeState.previousEffective.normalizedState) : '暂无上一有效状态' }}</strong><span>{{ formatTime(runtimeState.previousEffective?.capturedMs) }}</span></div>
                <div><small>AP 有效变化记录</small><strong>{{ runtimeState.recent10?.length || 0 }} / 10</strong><span>按当前来源视图聚合</span></div>
              </div>
              <div v-if="runtimeState.recent10?.length" class="table-wrap wireless-ap-history"><table class="data-table"><thead><tr><th>AP 变化时间</th><th>前一状态</th><th>新状态</th><th>来源 AC</th></tr></thead><tbody><tr v-for="change in runtimeState.recent10" :key="change.id"><td>{{ formatTime(change.capturedMs) }}</td><td>{{ stateText(change.previousState) }}</td><td>{{ stateText(change.normalizedState) }}</td><td>{{ change.sources.map(source => source.sourceAcDeviceId).join(' / ') }}</td></tr></tbody></table></div>
              <article v-for="source in runtimeState.sources" :key="source.sourceAcDeviceId" class="wireless-source-history">
                <div class="wireless-member-card__top"><strong>{{ source.sourceAcDeviceId }}</strong><span :class="['state-pill', stateClass(source.normalizedState)]">{{ stateText(source.normalizedState) }}</span></div>
                <div class="wireless-state-values">
                  <div><small>最近有效状态</small><strong>{{ source.current ? stateText(source.current.normalizedState) : '暂无有效证据' }}</strong><span>{{ formatTime(source.current?.capturedMs) }}</span></div>
                  <div><small>上一有效状态</small><strong>{{ source.previousEffective ? stateText(source.previousEffective.normalizedState) : '暂无上一有效状态' }}</strong><span>{{ formatTime(source.previousEffective?.capturedMs) }}</span></div>
                  <div><small>最近采集结果</small><strong>{{ outcomeText(source.latestOutcome) }}</strong><span>{{ formatTime(source.latestCapturedMs) }}</span></div>
                </div>
                <el-alert v-if="source.historyStatus !== 'RECENT10_COMPLETE'" title="历史读取不完整，以下记录可能不足最近十次有效变化" type="warning" :closable="false" />
                <div v-if="source.recent10.length" class="table-wrap"><table class="data-table wireless-history-table"><thead><tr><th>变化时间</th><th>前一状态</th><th>新状态</th><th>来源修订</th></tr></thead><tbody><tr v-for="change in source.recent10" :key="change.id"><td>{{ formatTime(change.capturedMs) }}</td><td>{{ stateText(change.previousState) }}</td><td>{{ stateText(change.normalizedState) }}</td><td class="mono">{{ change.sourceRevision }}</td></tr></tbody></table></div>
                <p v-else class="wireless-runtime-note">暂无有效状态变化</p>
              </article>
              <div v-if="!runtimeState.sources.length" class="empty-state"><div><strong>身份尚未确认，无法关联状态历史</strong><small>请先核对 Serial 与完整 MAC 证据。</small></div></div>
            </template>
          </section>
        </template>
        <div v-else class="card empty-state"><div><strong>{{ partialError ? '无线业务部分可用' : '暂无无线业务数据' }}</strong><small>{{ partialError || 'AC Domain API 返回空集合。' }}</small></div></div>
      </main>
    </div>
    <WirelessOperationalCapabilities :domain-id="selectedDomainId" :resource-id="selectedResource?.id || ''" />
  </NcLayout>
</template>

<style scoped>
.wireless-layout { display: grid; grid-template-columns: 250px minmax(0, 1fr); gap: 14px; align-items: start; }
.wireless-sidebar { position: sticky; top: 0; min-height: 460px; }
.wireless-tree { padding: 6px 8px 12px; }
.wireless-tree__domain { margin-bottom: 6px; }
.wireless-tree__item, .wireless-tree__member { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 8px; border: 0; border-radius: 7px; padding: 9px 10px; color: var(--nc-text); background: transparent; text-align: left; cursor: pointer; }
.wireless-tree__item:hover, .wireless-tree__member:hover, .wireless-tree__item.active, .wireless-tree__member.active { background: var(--nc-primary-soft); }
.wireless-tree__item strong, .wireless-tree__item small { display: block; }
.wireless-tree__item small, .wireless-tree__member { color: var(--nc-text-secondary); font-size: 11px; }
.wireless-tree__member { padding: 7px 10px 7px 20px; font-size: 12px; }
.tree-branch { display: inline-block; width: 15px; color: var(--nc-text-muted); }
.wireless-main { min-width: 0; display: grid; gap: 14px; }
.wireless-task, .wireless-task-history { padding: 14px; }
.wireless-task-quick { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.wireless-task-row button { border: 0; background: transparent; color: var(--nc-text); cursor: pointer; padding: 8px 0; text-align: left; }
.wireless-task-detail { border-top: 1px solid var(--nc-border); margin-top: 8px; padding-top: 8px; overflow-wrap: anywhere; }
.wireless-domain-header { display: flex; justify-content: space-between; gap: 12px; padding: 18px 20px; }
.wireless-domain-header h2 { margin: 2px 0 4px; font-size: 20px; }
.wireless-subtitle { margin: 0; color: var(--nc-text-secondary); font-size: 12px; }
.wireless-domain-meta, .wireless-toolbar { display: flex; align-items: center; gap: 8px; }
.wireless-stats { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.stat-card__value--small { font-size: 15px; }
.wireless-member-grid, .wireless-bottom-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; padding: 0 14px 14px; }
.wireless-member-card { border: 1px solid var(--nc-border); border-radius: 7px; padding: 12px; }
.wireless-member-card__top, .wireless-member-card__line { display: flex; justify-content: space-between; gap: 10px; }
.wireless-member-card__line { margin-top: 8px; color: var(--nc-text-secondary); font-size: 11px; }
.wireless-member-card__line span:first-child { color: var(--nc-text-muted); }
.wireless-member-card code { color: var(--nc-text); font-size: 10px; }
.wireless-table tr { cursor: pointer; }
.wireless-table tr.selected td { background: var(--nc-primary-soft); }
.wireless-identity-values { grid-template-columns: repeat(3, minmax(0, 1fr)); margin: 0 14px; }
.wireless-identity-provenance { display: grid; grid-template-columns: minmax(100px, 0.25fr) minmax(0, 1fr); gap: 7px 12px; margin: 0 14px 12px; font-size: 11px; }
.wireless-identity-provenance dt { color: var(--nc-text-muted); }
.wireless-identity-provenance dd { margin: 0; overflow-wrap: anywhere; }
.wireless-toolbar input { width: 190px; border: 1px solid var(--nc-border); border-radius: 5px; padding: 6px 8px; color: var(--nc-text); background: var(--nc-surface); font-size: 11px; }
.source-tag { display: inline-block; margin: 1px 3px 1px 0; border-radius: 4px; padding: 2px 4px; color: var(--nc-primary); background: var(--nc-primary-soft); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.wireless-group-list, .wireless-observation-list { padding: 0 14px 14px; }
.wireless-group-row, .wireless-observation { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 10px; border: 0; border-bottom: 1px solid var(--nc-border); padding: 10px 2px; color: var(--nc-text-secondary); background: transparent; font: inherit; font-size: 11px; text-align: left; }
.wireless-group-row { cursor: pointer; }
.wireless-group-row > span:first-child { min-width: 0; overflow-wrap: anywhere; }
.wireless-group-row__status { flex-shrink: 0; text-align: right; }
.wireless-group-row:hover, .wireless-group-row.selected { background: var(--nc-primary-soft); }
.wireless-group-row:last-child, .wireless-observation:last-child { border-bottom: 0; }
.wireless-observation { flex-wrap: wrap; }
.wireless-observation__evidence { flex-basis: 100%; color: var(--nc-text-muted); }
.wireless-observation__evidence summary { width: fit-content; cursor: pointer; }
.wireless-observation__evidence dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px 10px; margin: 8px 0 0; overflow-wrap: anywhere; }
.wireless-observation__evidence dd { margin: 0; color: var(--nc-text-secondary); }
.wireless-group-row strong, .wireless-group-row small, .wireless-observation strong, .wireless-observation small { display: block; }
.wireless-group-row small { margin-top: 3px; color: var(--nc-text-muted); }
.wireless-group-detail { margin: 0 14px 14px; border: 1px solid var(--nc-border); border-radius: 7px; padding: 12px; color: var(--nc-text-secondary); font-size: 11px; }
.wireless-group-detail .card__header { padding: 0 0 8px; }
.wireless-group-detail h3 { margin: 0; color: var(--nc-text); font-size: 13px; }
.wireless-group-detail__meta { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 7px; }
.wireless-group-detail code { color: var(--nc-text); font-size: 12px; }
.wireless-groups .wireless-group-row { font-size: 13px; }
.wireless-groups small { font-size: 12px; }
.wireless-groups .wireless-group-detail { font-size: 13px; line-height: 1.6; }
.wireless-group-detail .warn-text { color: var(--nc-warning); }
.wireless-group-provenance { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 7px 12px; }
.wireless-group-provenance dt { color: var(--nc-text-muted); }
.wireless-group-provenance dd { margin: 0; overflow-wrap: anywhere; }
.wireless-collection-list { padding: 0 14px 8px; }
.wireless-collection-row { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 10px; border: 0; border-bottom: 1px solid var(--nc-border); padding: 10px 2px; color: var(--nc-text-secondary); background: transparent; font: inherit; font-size: 11px; text-align: left; cursor: pointer; }
.wireless-collection-row:hover, .wireless-collection-row.selected { background: var(--nc-primary-soft); }
.wireless-collection-row strong, .wireless-collection-row small { display: block; }
.wireless-collection-row small { margin-top: 3px; color: var(--nc-text-muted); }
.wireless-collection-detail { margin: 0 14px 14px; border: 1px solid var(--nc-border); border-radius: 7px; padding: 12px; color: var(--nc-text-secondary); font-size: 11px; }
.wireless-collection-detail .card__header { padding: 0 0 8px; }
.wireless-collection-detail h3 { margin: 0; color: var(--nc-text); font-size: 13px; }
.wireless-collection-detail__meta { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 7px; }
.wireless-collection-detail code { color: var(--nc-text); font-size: 10px; }
.wireless-collection-item { display: flex; justify-content: space-between; gap: 10px; border-top: 1px solid var(--nc-border); padding: 7px 0; }
.wireless-collection-item strong, .wireless-collection-item small { display: block; }
.wireless-collection-item small { margin-top: 3px; color: var(--nc-text-muted); }
.wireless-observation > div { display: flex; align-items: center; gap: 7px; }
.wireless-observation small { margin-top: 3px; color: var(--nc-text-muted); }
.wireless-runtime { min-width: 0; }
.wireless-runtime-note { margin: 12px 16px; color: var(--nc-text-secondary); font-size: 12px; }
.wireless-source-history { margin: 12px 16px; border: 1px solid var(--nc-border); border-radius: 7px; padding: 14px; }
.wireless-state-values { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 14px 0; }
.wireless-state-values small, .wireless-state-values strong, .wireless-state-values span { display: block; }
.wireless-state-values small, .wireless-state-values span { color: var(--nc-text-secondary); font-size: 12px; }
.wireless-state-values strong { margin: 5px 0; font-size: 14px; }
.wireless-ap-state-values, .wireless-ap-history { margin: 14px 16px; }
@media (max-width: 760px) { .wireless-state-values { grid-template-columns: 1fr; } }
@media (max-width: 1100px) { .wireless-layout { grid-template-columns: 210px minmax(0, 1fr); } .wireless-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 760px) { .wireless-layout { grid-template-columns: 1fr; } .wireless-sidebar { position: static; min-height: 0; } .wireless-domain-header, .wireless-toolbar { align-items: flex-start; flex-direction: column; } .wireless-member-grid, .wireless-bottom-grid { grid-template-columns: 1fr; } }
</style>
