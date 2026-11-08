<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { getH3cLldpClaims, type H3cLldpClaims } from '../api/client'

const props = defineProps<{ profileId: string; deviceId: string; siteId: string }>()
defineEmits<{ openTopology: [] }>()
const claims = ref<H3cLldpClaims | null>(null)
const loading = ref(false)
const error = ref('')
let sequence = 0
const rows = computed(() => claims.value?.relations.filter(row => row.profileId === props.profileId &&
  row.localDeviceId === props.deviceId && row.siteId === props.siteId) ?? [])
const time = (ms: number) => new Date(ms).toLocaleString('zh-CN', { hour12: false })
async function refresh() {
  const request = ++sequence
  claims.value = null; error.value = ''; loading.value = false
  if (!props.profileId || !props.deviceId || !props.siteId) return
  const profileId = props.profileId
  loading.value = true
  try {
    const next = await getH3cLldpClaims(profileId, { history: false, limit: 1000 })
    if (request !== sequence) return
    if (next.profileId !== profileId) throw new Error('PROFILE_MISMATCH')
    claims.value = next
  } catch { if (request === sequence) error.value = 'LLDP 邻居读取失败，请重试。' }
  finally { if (request === sequence) loading.value = false }
}
watch(() => [props.profileId, props.deviceId, props.siteId], refresh, { immediate: true })
onBeforeUnmount(() => { ++sequence })
</script>

<template>
  <section class="detail-section" data-testid="device-lldp-neighbors">
    <div class="detail-section__head"><h3>接口 / LLDP 邻居</h3><div><el-button :disabled="loading || !profileId" size="small" @click="refresh">刷新邻居证据</el-button><el-button size="small" @click="$emit('openTopology')">查看拓扑工作台</el-button></div></div>
    <p class="muted">按设备、连接档案和捕获 Site 精确匹配。LLDP 是采集时的邻居声明，不推断上下游方向或实时连通性。</p>
    <p v-if="!profileId || !siteId" class="detail-empty">未关联可用连接资料或明确 Site，暂无可核对的邻居证据。</p>
    <p v-else-if="loading" role="status">正在读取 LLDP 邻居证据…</p>
    <p v-else-if="error" role="alert">{{ error }}</p>
    <template v-else-if="claims">
      <p class="muted">{{ claims.scopeState }} · claims {{ claims.claimsRevision || '—' }} · {{ rows.length }} 条当前事实</p>
      <p v-if="claims.relationCount >= 1000" class="muted">结果达到查询上限，可能不完整。</p>
      <div v-if="rows.length" class="table-wrap"><table class="data-table" aria-label="设备 LLDP 邻居表"><thead><tr><th>本地接口</th><th>邻居 / 端口</th><th>邻居管理地址</th><th>捕获时间 / 来源</th><th>Site / revision</th></tr></thead><tbody><tr v-for="row in rows" :key="`${row.relationKey}:${row.factRevision}`"><td>{{ row.localInterface }}</td><td>{{ row.neighborDevice }}<br />{{ row.neighborPort || '端口未知' }}</td><td>{{ row.neighborManagementAddress || '地址未知' }}</td><td>{{ time(row.capturedMs) }}<br />{{ row.sourceAuthority || row.source }}<br />{{ row.source }}</td><td>{{ row.siteId }} · {{ row.changeKind }}<br />{{ row.factRevision }}<details><summary>捕获范围</summary><span class="mono">{{ row.capturedScopeKey || 'UNSCOPED' }}</span></details></td></tr></tbody></table></div>
      <p v-else class="detail-empty">该设备在当前 Site 暂无 LLDP 邻居事实。</p>
    </template>
  </section>
</template>
