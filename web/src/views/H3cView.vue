<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getH3cFacts, getH3cLldpClaims, getH3cLldpRelations, getH3cProfiles, type H3cFact, type H3cLldpClaims, type H3cLldpRelation, type TerminalDevice } from '../api/client'

const profiles = ref<TerminalDevice[]>([])
const selected = ref<TerminalDevice | null>(null)
const facts = ref<H3cFact[]>([])
const vlanFacts = ref<H3cFact[]>([])
const relations = ref<H3cLldpRelation[]>([])
const claims = ref<H3cLldpClaims | null>(null)
const history = ref(false)
const loading = ref(false)
const factsError = ref('')
let selectionGeneration = 0
let factsRequestGeneration = 0
const selectedFacts = computed(() => facts.value)
const decode = (fact: H3cFact): Record<string, any> => {
  try { return JSON.parse(fact.payloadJson) as Record<string, any> } catch { return { output: fact.payloadJson } }
}
const of = (kind: string): H3cFact[] => selectedFacts.value.filter(fact => fact.kind === kind)
const time = (value?: number | null): string => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
const statusLabel = (fact: H3cFact): string => ({
  OFFLINE_REAL_FIXTURE_VERIFIED: '离线黄金样本已验证',
  EMPTY: '无数据', UNSUPPORTED: '命令不支持/未识别', PARTIAL: '部分输出', FAILED: '采集失败',
  STALE: '已采集（按捕获时间查看）', LIVE_VERIFIED: '实时采集成功',
} as Record<string, string>)[fact.dataState || fact.status] || fact.status

interface VlanRow {
  vlanId: number
  name: string | null
  description: string | null
  type: string | null
  routeInterface: boolean | null
  taggedPorts: string[]
  untaggedPorts: string[]
}
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const nullableText = (value: unknown): boolean => value === null || typeof value === 'string'
const stringList = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
const vlanRow = (value: unknown): value is VlanRow => record(value) && Number.isInteger(value.vlanId) &&
  Number(value.vlanId) >= 1 && Number(value.vlanId) <= 4094 && nullableText(value.name) && nullableText(value.description) &&
  nullableText(value.type) && (value.routeInterface === null || typeof value.routeInterface === 'boolean') &&
  stringList(value.taggedPorts) && stringList(value.untaggedPorts)
function vlanSnapshot(fact: H3cFact) {
  let payload: Record<string, unknown> = {}
  try { const value: unknown = JSON.parse(fact.payloadJson); if (record(value)) payload = value } catch { /* shown as unrecognized */ }
  const validPayload = payload.command === 'display vlan all' && Array.isArray(payload.vlans)
  const rows = validPayload ? (payload.vlans as unknown[]).filter(vlanRow) : []
  const completeRows = validPayload && rows.length === (payload.vlans as unknown[]).length
  let label = '未识别或不支持', tone = 'warn', visibleRows: VlanRow[] = []
  if (['FAILED', 'CANCELLED', 'INTERRUPTED', 'TIMED_OUT'].includes(fact.status)) { label = '采集失败'; tone = 'bad' }
  else if (fact.status === 'EMPTY' || (validPayload && payload.status === 'EMPTY')) label = '无数据'
  else if (fact.status !== 'UNSUPPORTED' && validPayload && ['PARSED', 'PARTIAL'].includes(String(payload.status))) {
    visibleRows = rows
    if (fact.status === 'PARTIAL' || payload.status === 'PARTIAL' || !completeRows) label = '部分解析'
    else if (rows.length && ['STALE', 'LIVE_VERIFIED'].includes(fact.status)) { label = '解析完成'; tone = 'good' }
    else if (!rows.length) label = '无数据'
    else visibleRows = []
  }
  return { fact, rows: visibleRows, label, tone, notice: typeof payload.notice === 'string' ? payload.notice : '' }
}
const vlanSnapshots = computed(() => vlanFacts.value.filter(fact => history.value || fact.isCurrent)
  .map(vlanSnapshot).sort((left, right) => right.fact.capturedMs - left.fact.capturedMs))

function clearFacts(): void { facts.value = []; vlanFacts.value = []; relations.value = []; claims.value = null }

async function loadFacts(profileId = selected.value?.id, generation = selectionGeneration): Promise<void> {
  const requestGeneration = ++factsRequestGeneration
  clearFacts()
  factsError.value = ''
  if (!profileId) { loading.value = false; return }
  loading.value = true
  try {
    const [nextFacts, nextVlanFacts, nextRelations, nextClaims] = await Promise.all([
      getH3cFacts(profileId, { history: history.value, limit: history.value ? 500 : 100 }),
      getH3cFacts(profileId, { kind: 'vlan.table', history: history.value, limit: history.value ? 500 : 100 }),
      getH3cLldpRelations(profileId, { history: history.value, limit: history.value ? 500 : 100 }),
      getH3cLldpClaims(profileId, { history: history.value, limit: history.value ? 500 : 100 }),
    ])
    if (generation === selectionGeneration && requestGeneration === factsRequestGeneration && selected.value?.id === profileId)
      {
        facts.value = nextFacts.filter(fact => fact.kind !== 'vlan.table')
        vlanFacts.value = nextVlanFacts.filter(fact => fact.kind === 'vlan.table')
        relations.value = nextRelations
        claims.value = nextClaims
      }
  } catch (error) {
    if (generation === selectionGeneration && requestGeneration === factsRequestGeneration && selected.value?.id === profileId) {
      factsError.value = '事实读取失败，请刷新重试。'
      ElMessage.error(error instanceof Error ? error.message : 'H3C 事实读取失败')
    }
  } finally {
    if (generation === selectionGeneration && requestGeneration === factsRequestGeneration && selected.value?.id === profileId)
      loading.value = false
  }
}
async function selectProfile(profile: TerminalDevice): Promise<void> {
  const generation = ++selectionGeneration
  selected.value = profile
  facts.value = []
  await loadFacts(profile.id, generation)
}
async function refresh(): Promise<void> {
  const generation = ++selectionGeneration
  clearFacts()
  factsError.value = ''
  loading.value = true
  try {
    const nextProfiles = await getH3cProfiles()
    if (generation !== selectionGeneration) return
    profiles.value = nextProfiles
    if (selected.value) selected.value = profiles.value.find(profile => profile.id === selected.value?.id) || null
    if (!selected.value && profiles.value.length) selected.value = profiles.value[0]
    await loadFacts(selected.value?.id, generation)
  } catch {
    if (generation === selectionGeneration) { factsError.value = '设备资料读取失败，请刷新重试。'; ElMessage.error(factsError.value) }
  } finally { if (generation === selectionGeneration) loading.value = false }
}
onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="H3C 事实回放" description="展示 H3C 只读采集事实，按捕获时间、来源与解析状态回放">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button size="small" @click="history = !history; loadFacts()">{{ history ? '仅当前' : '查看历史' }}</el-button></template>
    <section class="card"><div class="card__body"><div v-if="!profiles.length" class="empty-state"><strong>暂无 H3C profile</strong><small>服务未配置 H3C profile 或当前环境没有事实回放。</small></div><div v-for="profile in profiles" :key="profile.id" class="overview-footer" :class="{ 'is-selected': selected?.id === profile.id }"><span><b>{{ profile.host }}</b> · {{ profile.username }}</span><el-button size="small" @click="selectProfile(profile)">查看事实</el-button></div></div></section>
    <p v-if="factsError" role="alert" class="data-quality-note">{{ factsError }}</p>
    <template v-if="selected">
      <section class="card"><div class="card__body"><div class="inventory-summary"><span class="summary-chip"><b>{{ selected.host }}</b></span><span class="summary-chip">{{ history ? '当前与历史' : '仅当前' }}</span><span class="summary-chip">{{ facts.length + vlanFacts.length }} 条事实</span></div><p class="data-quality-note">数据时间与来源按每条事实显示。历史记录不代表设备当前实时状态，离线样本保留其来源标记。</p></div></section>
      <section class="card"><div class="card__header"><h2>设备身份 / Mesh Profile</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>事实</th><th>状态</th><th>时间</th><th>来源</th><th>数据</th></tr></thead><tbody><tr v-for="fact in [...of('device.identity'), ...of('mesh.profile')]" :key="fact.id"><td>{{ fact.kind }}</td><td>{{ statusLabel(fact) }}</td><td>{{ time(fact.capturedMs) }}</td><td>{{ fact.source }}</td><td class="mono">{{ JSON.stringify(decode(fact)) }}</td></tr><tr v-if="!of('device.identity').length && !of('mesh.profile').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
      <section class="card"><div class="card__header"><h2>Radio</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>接口</th><th>状态</th><th>模式</th><th>信道</th><th>时间 / 来源</th></tr></thead><tbody><tr v-for="fact in of('radio')" :key="fact.id"><td>{{ decode(fact).interfaceName }}</td><td>{{ decode(fact).linkStatus }}</td><td>{{ decode(fact).radioMode || '—' }}</td><td>{{ decode(fact).channel || '—' }}</td><td>{{ time(fact.capturedMs) }} · {{ fact.source }}</td></tr><tr v-if="!of('radio').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
      <section class="card"><div class="card__header"><h2>接口 / LLDP / Mesh Peer</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>类型</th><th>稳定键</th><th>状态</th><th>关键字段</th><th>时间 / 来源</th></tr></thead><tbody><tr v-for="fact in [...of('interface'), ...of('lldp.neighbor'), ...of('mesh.peer')]" :key="fact.id"><td>{{ fact.kind }}</td><td class="mono">{{ fact.identityKey }}</td><td>{{ statusLabel(fact) }}</td><td class="mono">{{ JSON.stringify(decode(fact)) }}</td><td>{{ time(fact.capturedMs) }} · {{ fact.source }}</td></tr><tr v-if="!of('interface').length && !of('lldp.neighbor').length && !of('mesh.peer').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
      <section class="card vlan-facts">
        <div class="card__header"><h2>VLAN 采集快照</h2><span class="state-pill">{{ vlanSnapshots.length }} 份快照</span></div>
        <div class="card__body">
          <p class="data-quality-note">仅展示 display vlan all 的采集事实，各次快照分别显示。需要采集时请前往任务中心创建只读计划。</p>
          <p v-if="!vlanSnapshots.length" class="muted">{{ loading ? '正在读取 VLAN 事实…' : factsError ? 'VLAN 事实暂不可用。' : '暂无 VLAN 采集快照。规划 VLAN 不作为实采数据展示。' }}</p>
          <article v-for="snapshot in vlanSnapshots" :key="snapshot.fact.id" class="vlan-snapshot">
            <div class="overview-footer"><h3>{{ snapshot.fact.isCurrent ? '当前快照' : '历史快照' }}</h3><span :class="['state-pill', snapshot.tone]">{{ snapshot.label }}</span></div>
            <p class="muted">捕获时间：{{ time(snapshot.fact.capturedMs) }} · 来源：{{ snapshot.fact.source }}</p>
            <p v-if="snapshot.fact.status === 'STALE'" class="muted">状态按捕获时间判断，此快照不代表设备当前实时状态。</p>
            <p v-if="snapshot.fact.collectedRawVerified" class="muted">Raw 完整性已校验，解析结果以上述状态为准。</p>
            <p v-if="snapshot.notice" class="data-quality-note">{{ snapshot.notice }}</p>
            <div class="table-wrap"><table class="data-table"><thead><tr><th>VLAN ID</th><th>名称</th><th>描述</th><th>类型</th><th>路由接口</th><th>Tagged 端口</th><th>Untagged 端口</th></tr></thead><tbody>
              <tr v-for="(vlan, index) in snapshot.rows" :key="`${vlan.vlanId}:${index}`"><td>{{ vlan.vlanId }}</td><td>{{ vlan.name || '—' }}</td><td>{{ vlan.description || '—' }}</td><td>{{ vlan.type || '—' }}</td><td>{{ vlan.routeInterface === null ? '未知' : vlan.routeInterface ? '有' : '无' }}</td><td>{{ vlan.taggedPorts.join('、') || '—' }}</td><td>{{ vlan.untaggedPorts.join('、') || '—' }}</td></tr>
              <tr v-if="!snapshot.rows.length"><td colspan="7" class="muted">{{ snapshot.label === '采集失败' ? '本次采集失败，没有可展示的 VLAN 数据。' : snapshot.label === '未识别或不支持' ? '未取得可识别的 VLAN 数据。' : '本快照没有可展示的 VLAN 行。' }}</td></tr>
            </tbody></table></div>
          </article>
        </div>
      </section>
      <section class="card"><div class="card__header"><h2>LLDP 关系与变化历史</h2><span class="state-pill">{{ relations.length }} 条</span></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>本地设备 / 接口</th><th>邻居设备 / 端口</th><th>Site</th><th>变化</th><th>时间 / 来源</th><th>Fact revision</th></tr></thead><tbody><tr v-for="relation in relations" :key="relation.factRevision"><td class="mono">{{ relation.localDeviceId }} / {{ relation.localInterface }}</td><td>{{ relation.neighborDevice }} / {{ relation.neighborPort || '—' }}</td><td>{{ relation.siteId }}</td><td>{{ relation.changeKind }}</td><td>{{ time(relation.capturedMs) }} · {{ relation.source }}</td><td class="mono">{{ relation.factRevision }}</td></tr><tr v-if="!relations.length"><td colspan="6" class="muted">没有带 Site scope 的 LLDP 关系事实</td></tr></tbody></table></div></section>
      <section class="card"><div class="card__header"><h2>LLDP Site claims</h2><span class="state-pill">{{ claims?.scopeState || '—' }}</span></div><div class="card__body"><p class="data-quality-note">当前作用域：{{ claims?.isGlobal ? '全局' : claims?.allowedSiteReferences.join('、') || '无授权 Site' }} · claims revision {{ claims?.claimsRevision || '—' }}</p></div></section>
    </template>
  </NcLayout>
</template>

<style scoped>
.vlan-snapshot + .vlan-snapshot { margin-top: 20px; padding-top: 16px; border-top: 1px solid var(--nc-border); }
</style>
