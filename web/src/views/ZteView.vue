<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { getZteFacts, getZteProfiles, getZteRun, startZteCollection, type ZteCollectionRun, type ZteFact, type ZteProfile } from '../api/client'
import NcLayout from '../components/NcLayout.vue'

const profiles = ref<ZteProfile[]>([])
const selected = ref<ZteProfile | null>(null)
const run = ref<ZteCollectionRun | null>(null)
const facts = ref<ZteFact[]>([])
const loading = ref(false)
let selectionGeneration = 0
const payload = (fact: ZteFact): unknown => { try { return JSON.parse(fact.payloadJson) } catch { return fact.payloadJson } }
const formatTime = (value?: number | null): string => value ? new Date(value).toLocaleString() : '—'
async function refresh(): Promise<void> {
  profiles.value = await getZteProfiles()
  if (selected.value && !profiles.value.some(profile => profile.id === selected.value?.id)) selected.value = null
  if (!selected.value && profiles.value.length) await selectProfile(profiles.value[0])
  if (selected.value) facts.value = await getZteFacts(selected.value.id, { history: false, limit: 50 })
}
async function selectProfile(profile: ZteProfile): Promise<void> {
  selectionGeneration += 1
  const generation = selectionGeneration
  selected.value = profile
  run.value = null
  const nextFacts = await getZteFacts(profile.id, { history: false, limit: 50 })
  if (generation === selectionGeneration) facts.value = nextFacts
}
async function collect(profile: ZteProfile): Promise<void> {
  loading.value = true
  try {
    await selectProfile(profile)
    const generation = selectionGeneration
    const result = await startZteCollection(profile.id)
    let currentRun = await getZteRun(result.runId)
    if (generation === selectionGeneration) run.value = currentRun
    for (let i = 0; i < 1200 && currentRun && ['QUEUED', 'RUNNING'].includes(currentRun.state); i++) {
      await new Promise(resolve => setTimeout(resolve, 500))
      currentRun = await getZteRun(result.runId)
      if (generation === selectionGeneration) run.value = currentRun
      if (generation !== selectionGeneration) break
    }
    if (generation === selectionGeneration) {
      facts.value = await getZteFacts(profile.id, { limit: 50 })
      ElMessage.success(currentRun?.state === 'SUCCEEDED' ? 'ZTE 采集完成' : `ZTE 采集状态：${currentRun?.state || 'UNKNOWN'}`)
    }
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : 'ZTE 采集失败') }
  finally { loading.value = false }
}
onMounted(() => { void refresh().catch(() => ElMessage.error('ZTE profile 读取失败')) })
</script>

<template>
  <NcLayout title="ZTE 采集" description="只读 ZXR10 profile、接口、光模块与 LLDP 当前快照；失败采集不会覆盖上一份有效数据">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button></template>
    <section class="card"><div class="card__body"><div v-if="!profiles.length" class="empty-state"><strong>暂无 ZTE profile</strong><small>请先通过受控 API 创建 profile；未配置凭据或 Host Key 时服务会拒绝认证。</small></div><div v-for="profile in profiles" :key="profile.id" class="overview-footer" :class="{ 'is-selected': selected?.id === profile.id }"><span><b>{{ profile.host }}</b> · {{ profile.username }} · {{ profile.siteId }}</span><span><span class="state-pill" :class="profile.enabled ? 'good' : 'neutral'">{{ profile.enabled ? '已启用' : '已禁用' }}</span><el-button size="small" @click="selectProfile(profile)">查看</el-button><el-button size="small" :disabled="!profile.enabled" :loading="loading && selected?.id === profile.id" @click="collect(profile)">采集</el-button></span></div></div></section>
    <section v-if="selected" class="card"><div class="card__body"><div class="inventory-summary"><span class="summary-chip"><b>{{ selected.host }}</b></span><span class="summary-chip">最近运行：{{ run?.state || '—' }}</span><span class="summary-chip">{{ formatTime(run?.endedMs || run?.startedMs) }}</span></div><p v-if="run?.failureReason" class="data-quality-note">采集失败：{{ run.failureReason }}；当前事实保持不变。</p><div class="table-wrap"><table class="data-table"><thead><tr><th>事实</th><th>状态</th><th>采集时间</th><th>数据</th></tr></thead><tbody><tr v-for="fact in facts" :key="fact.kind"><td>{{ fact.kind }}</td><td>{{ fact.status }}</td><td>{{ formatTime(fact.capturedMs) }}</td><td><pre class="mono">{{ JSON.stringify(payload(fact), null, 2) }}</pre></td></tr><tr v-if="!facts.length"><td colspan="4" class="muted">暂无当前事实</td></tr></tbody></table></div></div></section>
  </NcLayout>
</template>
