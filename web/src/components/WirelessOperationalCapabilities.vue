<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue'
import { getWirelessOperationalCapabilities, type WirelessOperationalCapabilityView } from '../api/client'

const props = withDefaults(defineProps<{ domainId?: string; resourceId?: string }>(), { domainId: '', resourceId: '' })
const snapshot = ref<WirelessOperationalCapabilityView | null>(null)
const loading = ref(false)
const error = ref('')
let generation = 0
let activated = false
const domains = computed(() => snapshot.value?.domains.filter(d => !props.domainId || d.domainId === props.domainId) || [])
const aps = computed(() => snapshot.value?.fitAps.filter(a => a.resourceId === props.resourceId) || [])
function freshness(capturedMs: number) {
  if (!snapshot.value || !Number.isFinite(capturedMs) || capturedMs > snapshot.value.asOf) return 'UNKNOWN'
  return snapshot.value.asOf - capturedMs > 300_000 ? 'STALE' : 'RECENT'
}
async function refresh() {
  const current = ++generation
  loading.value = true; error.value = ''; snapshot.value = null
  try { const result = await getWirelessOperationalCapabilities(); if (current === generation) snapshot.value = result }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '能力证据加载失败' }
  finally { if (current === generation) loading.value = false }
}
onMounted(() => { void refresh() })
watch(() => [props.domainId, props.resourceId], () => { void refresh() })
onActivated(() => { if (activated) void refresh(); activated = true })
onDeactivated(() => { ++generation; snapshot.value = null; loading.value = false })
onBeforeUnmount(() => { ++generation })
</script>

<template>
  <section class="card capability-panel" aria-label="无线运行能力证据">
    <div class="card__header"><h2>无线运行能力证据</h2><button class="btn btn--secondary" :disabled="loading" @click="refresh">刷新能力证据</button></div>
    <div class="card__body">
      <p v-if="loading" role="status">正在加载能力证据…</p>
      <p v-else-if="error" role="alert">{{ error }}</p>
      <template v-else-if="snapshot">
        <p class="capability-note">拓扑确认、设备观察与 HA 切换验证分别记录。AP 主备观察不代表控制器切换或业务恢复已验证。</p>
        <div v-if="domains.length" class="table-wrap"><table class="data-table"><thead><tr><th>AC Domain</th><th>拓扑证据</th><th>角色证据</th><th>HA 切换</th></tr></thead><tbody>
          <tr v-for="domain in domains" :key="domain.domainId"><td>{{ domain.domainId }}<small>{{ domain.domainKind }}</small></td><td>{{ domain.topologyEvidence }}<small v-for="reference in domain.topologyEvidenceRefs" :key="reference">{{ reference }}</small></td><td>{{ domain.deviceRoleEvidence }}<small>{{ domain.observations.length }} 条 AP 观察</small><details v-if="domain.observations.length"><summary>查看各 AC 角色证据</summary><ul><li v-for="item in domain.observations" :key="item.observationId">{{ item.acDeviceId }} · {{ item.rawState }} → {{ item.normalizedState }} · {{ freshness(item.capturedMs) }}<small>{{ new Date(item.capturedMs).toLocaleString() }} · Raw {{ item.rawManifestRef }} · Revision {{ item.sourceRevision }}</small></li></ul></details></td><td>{{ domain.haVerification }}<small>{{ domain.reason }}</small></td></tr>
        </tbody></table></div>
        <p v-else class="empty-state">当前范围暂无 AC Domain 能力证据。</p>
        <div v-for="ap in aps" :key="ap.resourceId" class="fit-evidence">
          <strong>{{ ap.displayName }}</strong><p>身份 {{ ap.identityStatus }} · AC 管理观察 {{ ap.acManagedState }} · 直接登录 {{ ap.directAccessState }}</p>
          <p>来源 AC：{{ ap.acDeviceIds.join(' / ') || 'UNKNOWN' }} · AP Group：{{ ap.apGroups.join(' / ') || 'UNKNOWN' }}</p>
          <p v-if="ap.identityConflict || ap.stateConflict" role="alert">{{ ap.identityConflict }} {{ ap.stateConflict }}</p>
          <details><summary>AP 观察来源</summary><ul><li v-for="item in ap.observations" :key="item.observationId">{{ item.acDeviceId }} · {{ item.rawState }} → {{ item.normalizedState }} · {{ freshness(item.capturedMs) }} · {{ new Date(item.capturedMs).toLocaleString() }}<small>Raw {{ item.rawManifestRef }} · Revision {{ item.sourceRevision }}</small></li></ul></details>
        </div>
        <p v-if="!resourceId" class="capability-note">选择物理 AP 查看身份、AC 管理状态及直接接入证据。</p>
        <p v-else-if="!aps.length" class="empty-state">当前范围暂无所选 AP 的直接或 AC 管理证据。</p>
        <small class="revision">证据时间 {{ new Date(snapshot.asOf).toLocaleString() }} · Revision {{ snapshot.sourceRevision }}</small>
      </template>
    </div>
  </section>
</template>

<style scoped>
.capability-panel { margin-top: 18px; }
small { display: block; overflow-wrap: anywhere; }
.capability-note, .revision { color: var(--text-secondary); }
.fit-evidence { padding: 16px 0; border-top: 1px solid var(--el-border-color, #ddd); }
td { vertical-align: top; max-width: 360px; overflow-wrap: anywhere; }
[role="alert"] { color: var(--el-color-danger); }
</style>
