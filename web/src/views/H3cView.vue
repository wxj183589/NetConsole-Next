<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getH3cFacts, getH3cProfiles, type H3cFact, type TerminalDevice } from '../api/client'

const profiles = ref<TerminalDevice[]>([])
const selected = ref<TerminalDevice | null>(null)
const facts = ref<H3cFact[]>([])
const history = ref(false)
const loading = ref(false)
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
  STALE: '历史有效（设备离线）', LIVE_VERIFIED: '实时采集成功',
} as Record<string, string>)[fact.dataState || fact.status] || fact.status

async function loadFacts(profileId = selected.value?.id, generation = selectionGeneration): Promise<void> {
  if (!profileId) { facts.value = []; return }
  const requestGeneration = ++factsRequestGeneration
  loading.value = true
  try {
    const nextFacts = await getH3cFacts(profileId, { history: history.value, limit: history.value ? 500 : 100 })
    if (generation === selectionGeneration && requestGeneration === factsRequestGeneration && selected.value?.id === profileId)
      facts.value = nextFacts
  } catch (error) {
    if (generation === selectionGeneration && requestGeneration === factsRequestGeneration && selected.value?.id === profileId)
      ElMessage.error(error instanceof Error ? error.message : 'H3C 事实读取失败')
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
  profiles.value = await getH3cProfiles()
  if (generation !== selectionGeneration) return
  if (selected.value) {
    const next = profiles.value.find(profile => profile.id === selected.value?.id)
    selected.value = next || null
  }
  if (!selected.value && profiles.value.length) selected.value = profiles.value[0]
  await loadFacts(selected.value?.id, generation)
}
onMounted(() => { void refresh().catch(() => ElMessage.error('H3C profile 读取失败')) })
</script>

<template>
  <NcLayout title="H3C 事实回放" description="展示 H3C V7/V9 只读事实；离线黄金样本明确标记为历史有效，不冒充实时采集">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button size="small" @click="history = !history; loadFacts()">{{ history ? '仅当前' : '查看历史' }}</el-button></template>
    <section class="card"><div class="card__body"><div v-if="!profiles.length" class="empty-state"><strong>暂无 H3C profile</strong><small>服务未配置 H3C profile 或当前环境没有事实回放。</small></div><div v-for="profile in profiles" :key="profile.id" class="overview-footer" :class="{ 'is-selected': selected?.id === profile.id }"><span><b>{{ profile.host }}</b> · {{ profile.username }}</span><el-button size="small" @click="selectProfile(profile)">查看事实</el-button></div></div></section>
    <template v-if="selected">
      <section class="card"><div class="card__body"><div class="inventory-summary"><span class="summary-chip"><b>{{ selected.host }}</b></span><span class="summary-chip">{{ history ? 'Current + History' : 'Current' }}</span><span class="summary-chip">{{ facts.length }} 条事实</span></div><p class="data-quality-note">数据时间：按每条事实显示。来源：按每条事实显示。车载 AP 当前离线；离线黄金样本不会冒充实时数据。</p></div></section>
      <section class="card"><div class="card__header"><h2>设备身份 / Mesh Profile</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>事实</th><th>状态</th><th>时间</th><th>来源</th><th>数据</th></tr></thead><tbody><tr v-for="fact in [...of('device.identity'), ...of('mesh.profile')]" :key="fact.id"><td>{{ fact.kind }}</td><td>{{ statusLabel(fact) }}</td><td>{{ time(fact.capturedMs) }}</td><td>{{ fact.source }}</td><td class="mono">{{ JSON.stringify(decode(fact)) }}</td></tr><tr v-if="!of('device.identity').length && !of('mesh.profile').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
      <section class="card"><div class="card__header"><h2>Radio</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>接口</th><th>状态</th><th>模式</th><th>信道</th><th>时间 / 来源</th></tr></thead><tbody><tr v-for="fact in of('radio')" :key="fact.id"><td>{{ decode(fact).interfaceName }}</td><td>{{ decode(fact).linkStatus }}</td><td>{{ decode(fact).radioMode || '—' }}</td><td>{{ decode(fact).channel || '—' }}</td><td>{{ time(fact.capturedMs) }} · {{ fact.source }}</td></tr><tr v-if="!of('radio').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
      <section class="card"><div class="card__header"><h2>接口 / LLDP / Mesh Peer</h2></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>类型</th><th>稳定键</th><th>状态</th><th>关键字段</th><th>时间 / 来源</th></tr></thead><tbody><tr v-for="fact in [...of('interface'), ...of('lldp.neighbor'), ...of('mesh.peer')]" :key="fact.id"><td>{{ fact.kind }}</td><td class="mono">{{ fact.identityKey }}</td><td>{{ statusLabel(fact) }}</td><td class="mono">{{ JSON.stringify(decode(fact)) }}</td><td>{{ time(fact.capturedMs) }} · {{ fact.source }}</td></tr><tr v-if="!of('interface').length && !of('lldp.neighbor').length && !of('mesh.peer').length"><td colspan="5" class="muted">无数据</td></tr></tbody></table></div></section>
    </template>
  </NcLayout>
</template>
