<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import {
  getWirelessAcDomains,
  getWirelessApGroups,
  getWirelessApResources,
  type WirelessAcDomain,
  type WirelessApGroup,
  type WirelessApResource,
} from '../api/client'

const loading = ref(false)
const domains = ref<WirelessAcDomain[]>([])
const resources = ref<WirelessApResource[]>([])
const groups = ref<WirelessApGroup[]>([])
const selectedDomainId = ref('')
const selectedAcId = ref('')
const selectedResourceId = ref('')
const search = ref('')
const error = ref('')
const groupError = ref('')
const warnings = ref<string[]>([])
const page = ref(1)
const pageSize = 20
const stateFilter = ref('')
let refreshGeneration = 0
let groupGeneration = 0

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
const visibleGroups = computed(() => groups.value.filter(group => !selectedAcId.value || group.sourceAcDeviceId === selectedAcId.value))
function viewObservations(resource: WirelessApResource) {
  return resource.observations.filter(item => (!selectedDomain.value || item.sourceAcDomainId === selectedDomain.value.id) && (!selectedAcId.value || item.sourceAcDeviceId === selectedAcId.value))
}
watch([search, stateFilter, selectedDomainId, selectedAcId], () => { page.value = 1; selectedResourceId.value = '' })
const domainApCount = computed(() => visibleResources.value.length)
const scopeLabel = computed(() => selectedDomain.value?.siteScopeStatus || '—')
const runtimeLabel = computed(() => {
  const states = selectedDomain.value?.members.map(member => member.runtimeState) || []
  if (states.includes('RUNNING_MASTER')) return 'RUNNING_MASTER'
  if (states.includes('RUNNING_BACKUP')) return 'RUNNING_BACKUP'
  return states[0] || 'UNKNOWN'
})

function selectDomain(domain: WirelessAcDomain): void {
  selectedDomainId.value = domain.id
  selectedAcId.value = ''
  selectedResourceId.value = ''
  void loadGroups(domain.id)
}
function selectAc(domain: WirelessAcDomain, deviceId: string): void {
  selectedDomainId.value = domain.id
  selectedAcId.value = deviceId
  selectedResourceId.value = ''
  void loadGroups(domain.id)
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
  try {
    const result = await readAll(params => getWirelessApGroups(domainId, params))
    if (generation === groupGeneration) groups.value = result
  } catch (cause) {
    if (generation === groupGeneration) groupError.value = cause instanceof Error ? cause.message : 'AP 组读取失败'
  }
}
async function refresh(): Promise<void> {
  const generation = ++refreshGeneration
  ++groupGeneration
  loading.value = true
  error.value = ''
  groupError.value = ''
  warnings.value = []
  try {
    const [domainItems, resourceItems] = await Promise.all([
      readAll(params => getWirelessAcDomains(params)),
      readAll(params => getWirelessApResources(params)),
    ])
    if (generation !== refreshGeneration) return
    domains.value = domainItems
    resources.value = resourceItems
    if (!domains.value.some(domain => domain.id === selectedDomainId.value)) selectedDomainId.value = domains.value[0]?.id || ''
    if (!selectedDomain.value?.members.some(member => member.deviceId === selectedAcId.value)) selectedAcId.value = ''
    page.value = 1
    selectedResourceId.value = ''
    if (selectedDomainId.value) await loadGroups(selectedDomainId.value)
    else groups.value = []
  } catch (cause) {
    if (generation !== refreshGeneration) return
    domains.value = []; resources.value = []; groups.value = []
    selectedDomainId.value = ''; selectedAcId.value = ''; selectedResourceId.value = ''
    error.value = cause instanceof Error ? cause.message : '无线业务数据读取失败'
    ElMessage.error(error.value)
  } finally { if (generation === refreshGeneration) loading.value = false }
}
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string {
  return ['RUNNING_MASTER', 'RUNNING_BACKUP', 'CONFIRMED'].includes(state) ? 'good' : ['UNKNOWN', 'STALE', 'CANDIDATE', 'EVIDENCE_PENDING'].includes(state) ? 'warn' : ['OFFLINE', 'CONFLICT'].includes(state) ? 'bad' : 'neutral'
}
function stateText(state: string): string {
  return ({ RUNNING_MASTER: '运行主', RUNNING_BACKUP: '运行备', OFFLINE: '离线', UNKNOWN: '未知', STALE: '过期', CONFLICT: '冲突', CANDIDATE: '候选', CONFIRMED: '已确认', EVIDENCE_PENDING: '证据待定' } as Record<string, string>)[state] || state
}

onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="无线业务" description="按 AC Domain 查看 FIT-AP 物理身份、来源 Observation、AP 组与主备运行证据">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新数据</el-button></template>

    <el-alert v-if="warnings.includes('OFFLINE_FIXTURE_EVIDENCE')" title="离线演示数据 · 非模拟器实时状态" type="warning" :closable="false" show-icon />
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

          <section class="card"><div class="card__header"><h2>FIT-AP</h2><div class="wireless-toolbar"><input v-model="search" aria-label="搜索 AP" placeholder="Serial / MAC / 名称搜索" /><select v-model="stateFilter" aria-label="筛选 AP 状态"><option value="">所有状态</option><option v-for="state in ['RUNNING_MASTER', 'RUNNING_BACKUP', 'OFFLINE', 'UNKNOWN', 'STALE', 'CONFLICT']" :key="state" :value="state">{{ stateText(state) }}</option></select><span class="state-pill info">{{ visibleResources.length }} 条</span></div></div><div v-if="visibleResources.length" class="table-wrap"><table class="data-table wireless-table"><thead><tr><th>物理 AP</th><th>Serial</th><th>完整 MAC</th><th>来源 AC</th><th>AP 组</th><th>状态</th><th>身份</th><th>最近观察</th></tr></thead><tbody><tr v-for="resource in pagedResources" :key="resource.id" :class="{ selected: selectedResource?.id === resource.id }" @click="selectedResourceId = resource.id"><td><span class="device-name">{{ resource.displayName }}</span><span class="device-subline mono">{{ resource.id }}</span></td><td class="mono">{{ resource.serialEvidence || '—' }}</td><td class="mono">{{ resource.normalizedMacSet.join(' / ') || '—' }}</td><td><span v-for="observation in viewObservations(resource)" :key="observation.id" class="source-tag">{{ observation.sourceAcDeviceId }}</span></td><td>{{ viewObservations(resource).map(observation => observation.apGroup).filter(Boolean).join(' / ') || '—' }}</td><td><span v-for="observation in viewObservations(resource)" :key="observation.id" :class="['state-pill', stateClass(observation.normalizedState)]">{{ stateText(observation.normalizedState) }}</span></td><td><span :class="['state-pill', stateClass(resource.identityStatus)]">{{ stateText(resource.identityStatus) }}</span></td><td>{{ formatTime(resource.lastSeenMs) }}</td></tr></tbody></table></div><div v-else class="empty-state"><div><strong>暂无可见 AP Observation</strong><small>当前筛选下没有 AP，请调整搜索或状态筛选。</small></div></div></section>

          <el-pagination v-if="visibleResources.length > pageSize" v-model:current-page="page" :page-size="pageSize" :total="visibleResources.length" layout="total, prev, pager, next" />
          <div class="wireless-bottom-grid">
            <section class="card"><div class="card__header"><h2>AP 组</h2><span class="state-pill">AC 作用域</span></div><div v-if="visibleGroups.length" class="wireless-group-list"><div v-for="group in visibleGroups" :key="group.id" class="wireless-group-row"><span><strong>{{ group.displayName }}</strong><small class="mono">{{ group.id }}</small></span><span>{{ group.memberResourceIds.length }} 个 AP</span></div></div><div v-else class="empty-state"><div><strong>{{ groupError ? 'AP 组读取失败' : '暂无可见 AP 组' }}</strong><small>{{ groupError || '按来源 AC 保留 AP 组。' }}</small></div></div></section>
            <section class="card"><div class="card__header"><h2>Observation 详情</h2><span class="state-pill info">{{ selectedResource ? viewObservations(selectedResource).length : 0 }} 条来源</span></div><el-alert v-if="selectedResource?.stateConflictReason" title="来源状态存在冲突，请核对各 AC 的原始观察" type="warning" :closable="false" /><div v-if="selectedResource" class="wireless-observation-list"><div v-for="observation in viewObservations(selectedResource)" :key="observation.id" class="wireless-observation"><div><strong>{{ observation.sourceAcDeviceId }}</strong><span class="state-pill" :class="stateClass(observation.normalizedState)">{{ stateText(observation.normalizedState) }}</span></div><small>{{ observation.sourceSiteId }} · {{ formatTime(observation.capturedMs) }} · {{ observation.rawManifestRef }}</small><small>raw={{ observation.rawState }} · parser={{ observation.parserRevision }} · {{ observation.relationStatus }}</small></div></div><div v-else class="empty-state"><div><strong>选择一条 AP</strong><small>查看 Physical AP Identity 与各 AC 独立 Observation。</small></div></div></section>
          </div>
        </template>
        <div v-else class="card empty-state"><div><strong>暂无无线业务数据</strong><small>AC Domain API 返回空集合。</small></div></div>
      </main>
    </div>
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
.wireless-toolbar input { width: 190px; border: 1px solid var(--nc-border); border-radius: 5px; padding: 6px 8px; color: var(--nc-text); background: var(--nc-surface); font-size: 11px; }
.source-tag { display: inline-block; margin: 1px 3px 1px 0; border-radius: 4px; padding: 2px 4px; color: var(--nc-primary); background: var(--nc-primary-soft); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.wireless-group-list, .wireless-observation-list { padding: 0 14px 14px; }
.wireless-group-row, .wireless-observation { display: flex; align-items: center; justify-content: space-between; gap: 10px; border-bottom: 1px solid var(--nc-border); padding: 10px 2px; color: var(--nc-text-secondary); font-size: 11px; }
.wireless-group-row:last-child, .wireless-observation:last-child { border-bottom: 0; }
.wireless-group-row strong, .wireless-group-row small, .wireless-observation strong, .wireless-observation small { display: block; }
.wireless-group-row small { margin-top: 3px; color: var(--nc-text-muted); }
.wireless-observation > div { display: flex; align-items: center; gap: 7px; }
.wireless-observation small { margin-top: 3px; color: var(--nc-text-muted); }
@media (max-width: 1100px) { .wireless-layout { grid-template-columns: 210px minmax(0, 1fr); } .wireless-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 760px) { .wireless-layout { grid-template-columns: 1fr; } .wireless-sidebar { position: static; min-height: 0; } .wireless-domain-header, .wireless-toolbar { align-items: flex-start; flex-direction: column; } .wireless-member-grid, .wireless-bottom-grid { grid-template-columns: 1fr; } }
</style>
