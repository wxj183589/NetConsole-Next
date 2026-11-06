<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Connection, Document, Refresh, Warning } from '@element-plus/icons-vue'
import NcLayout from '../components/NcLayout.vue'
import {
  getDeviceInventory,
  getH3cLldpClaims,
  getH3cProfiles,
  type DeviceInventoryItem,
  type DeviceInventoryPage,
  type H3cLldpClaims,
  type H3cLldpRelation,
  type TerminalDevice,
} from '../api/client'

interface TopologyRelation extends H3cLldpRelation {
  profileHost: string
}

const loading = ref(false)
const errorMessage = ref('')
const inventory = ref<DeviceInventoryPage | null>(null)
const relations = ref<TopologyRelation[]>([])
const claims = ref<H3cLldpClaims[]>([])
const partialErrors = ref<string[]>([])

const assets = computed<DeviceInventoryItem[]>(() => inventory.value?.items || [])
const summary = computed(() => inventory.value?.summary || {
  totalRecords: 0, validDevices: 0, invalidRecords: 0, enabledDevices: 0,
  disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 0,
})
const siteCount = computed(() => new Set(assets.value.map(item => item.siteId || item.site).filter(Boolean)).size)
const scopeLabel = computed(() => {
  const states = [...new Set(claims.value.map(item => item.scopeState).filter(Boolean))]
  if (!states.length) return '暂无 LLDP scope 事实'
  return states.join(' / ')
})
const relationCount = computed(() => relations.value.length)
const loadedEmpty = computed(() => !loading.value && !errorMessage.value && !assets.value.length && !relations.value.length)

function formatTime(value?: number | null): string {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}
function statusLabel(value: string): string {
  return ({ ONLINE: '最近在线', OFFLINE: '最近离线', UNKNOWN: '状态未知', ENABLED: '已启用', DISABLED: '已禁用' } as Record<string, string>)[value] || value || '状态未知'
}
function statusClass(value: string): string {
  return value === 'ONLINE' || value === 'ENABLED' ? 'good' : value === 'OFFLINE' || value === 'DISABLED' ? 'bad' : 'neutral'
}
function errorText(cause: unknown, fallback: string): string {
  return cause instanceof Error ? cause.message : fallback
}
function profileRelations(profile: TerminalDevice, result: PromiseSettledResult<H3cLldpClaims>): void {
  if (result.status === 'fulfilled') {
    claims.value.push(result.value)
    relations.value.push(...result.value.relations.map(relation => ({ ...relation, profileHost: profile.host })))
  } else {
    partialErrors.value.push(`${profile.host} 的 LLDP 关系读取失败`)
  }
}
async function refresh(): Promise<void> {
  loading.value = true
  errorMessage.value = ''
  partialErrors.value = []
  inventory.value = null
  claims.value = []
  relations.value = []
  try {
    const [inventoryResult, profilesResult] = await Promise.all([getDeviceInventory({ page: 1, pageSize: 100 }), getH3cProfiles()])
    inventory.value = inventoryResult
    const relationResults = await Promise.allSettled(profilesResult.map(profile => getH3cLldpClaims(profile.id, { history: false, limit: 1000 })))
    relationResults.forEach((result, index) => profileRelations(profilesResult[index], result))
  } catch (cause) {
    errorMessage.value = errorText(cause, '拓扑与资产数据读取失败，请刷新重试。')
    ElMessage.error(errorMessage.value)
  } finally {
    loading.value = false
  }
}

onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="拓扑与资产" description="设备库存与 LLDP 关系均来自服务端授权范围，未采集或读取失败的数据不会被推断为在线或邻居关系。">
    <template #actions><el-button size="small" :loading="loading" @click="refresh"><el-icon><Refresh /></el-icon>刷新</el-button></template>

    <div v-if="errorMessage" class="card topology-error" role="alert">
      <div class="empty-state"><el-icon><Warning /></el-icon><strong>拓扑与资产读取失败</strong><small>{{ errorMessage }}</small><el-button size="small" @click="refresh">重试</el-button></div>
    </div>
    <template v-else>
      <div v-if="loading && !inventory" class="card topology-loading" aria-live="polite"><div class="empty-state"><el-icon><Connection /></el-icon><strong>正在读取拓扑与资产</strong><small>正在读取服务端库存、授权 scope 和 LLDP 关系。</small></div></div>
      <template v-else>
        <div class="inventory-summary" data-testid="topology-summary">
          <span class="summary-chip"><b>{{ summary.totalRecords }}</b> 台资产</span>
          <span class="summary-chip summary-chip--success"><b>{{ summary.onlineDevices }}</b> 最近在线</span>
          <span class="summary-chip summary-chip--danger"><b>{{ summary.offlineDevices }}</b> 最近离线</span>
          <span class="summary-chip"><b>{{ summary.unknownStatus }}</b> 状态未知</span>
          <span class="summary-chip summary-chip--info"><b>{{ relationCount }}</b> 条 LLDP 关系</span>
          <span class="summary-chip"><b>{{ siteCount }}</b> 个授权局点</span>
        </div>
        <div v-if="partialErrors.length" class="data-quality-note" role="status"><el-icon><Warning /></el-icon>{{ partialErrors.join('；') }}。已展示其他成功读取的关系。</div>
        <section class="card topology-scope-card">
          <div class="card__header"><div><h2>LLDP 作用域</h2><p>scope 状态由服务端 claims 返回；页面不根据名称或地址猜测归属。</p></div><span class="state-pill info">{{ scopeLabel }}</span></div>
          <div class="card__body overview-footer"><span v-for="item in claims" :key="item.profileId"><b>{{ item.profileId }}</b> · {{ item.relationCount }} 条关系 · revision {{ item.claimsRevision || '—' }}</span><span v-if="!claims.length" class="muted">暂无可展示的 LLDP scope 事实</span></div>
        </section>
        <section class="card">
          <div class="card__header"><div><h2>资产清单</h2><p>仅展示当前授权范围内服务端库存页，状态未知保持未知。</p></div><span class="state-pill">{{ assets.length }} / {{ inventory?.total || 0 }}</span></div>
          <div class="card__body table-wrap"><table class="data-table"><thead><tr><th>状态</th><th>设备</th><th>管理地址</th><th>厂商 / 类型</th><th>局点 / 分组</th><th>更新时间</th></tr></thead><tbody><tr v-for="asset in assets" :key="asset.id"><td><span :class="['state-pill', statusClass(asset.connectionStatus)]">{{ statusLabel(asset.connectionStatus) }}</span></td><td><b>{{ asset.name }}</b><small class="device-subline mono">{{ asset.deviceId || asset.id }}</small></td><td class="mono">{{ asset.managementIp || asset.managementAddress }}:{{ asset.port }}</td><td>{{ asset.vendor }}<small class="device-subline">{{ asset.deviceType }} · {{ asset.model || '型号未知' }}</small></td><td>{{ asset.site }}<small class="device-subline">{{ asset.group || '未分组' }}</small></td><td>{{ formatTime(asset.updatedAt || asset.lastSeenMs) }}</td></tr><tr v-if="!assets.length"><td colspan="6"><div class="empty-state"><el-icon><Document /></el-icon><strong>暂无资产</strong><small>当前授权范围没有服务端设备库存记录。</small></div></td></tr></tbody></table></div>
        </section>
        <section class="card">
          <div class="card__header"><div><h2>LLDP 邻居关系</h2><p>关系来源、捕获时间和 revision 保留在每一行，避免把历史或未知状态当作当前拓扑。</p></div><span class="state-pill info">{{ relationCount }} 条</span></div>
          <div class="card__body table-wrap"><table class="data-table"><thead><tr><th>来源设备</th><th>本地接口</th><th>邻居设备 / 端口</th><th>邻居管理地址</th><th>Site</th><th>捕获时间 / 来源</th></tr></thead><tbody><tr v-for="relation in relations" :key="`${relation.profileId}:${relation.relationKey}:${relation.factRevision}`"><td><b>{{ relation.profileHost }}</b><small class="device-subline mono">{{ relation.profileId }}</small></td><td class="mono">{{ relation.localInterface }}</td><td>{{ relation.neighborDevice }}<small class="device-subline mono">{{ relation.neighborPort || '端口未知' }}</small></td><td class="mono">{{ relation.neighborManagementAddress || '地址未知' }}</td><td>{{ relation.siteId || 'Site 未知' }}</td><td>{{ formatTime(relation.capturedMs) }}<small class="device-subline">{{ relation.source }} · {{ relation.changeKind }} · {{ relation.factRevision }}</small></td></tr><tr v-if="!relations.length"><td colspan="6"><div class="empty-state"><el-icon><Connection /></el-icon><strong>暂无 LLDP 关系</strong><small>当前授权范围没有带 scope 的 LLDP 关系事实，页面不会绘制推断拓扑。</small></div></td></tr></tbody></table></div>
        </section>
        <div v-if="loadedEmpty" class="card" data-testid="topology-empty"><div class="empty-state"><strong>当前工作区暂无可用拓扑或资产数据</strong><small>服务端返回了空库存和空 LLDP 关系。</small></div></div>
      </template>
    </template>
  </NcLayout>
</template>

<style scoped>
.topology-loading, .topology-error { margin-bottom: 10px; }
.topology-error .empty-state { color: var(--nc-danger); }
.topology-error .empty-state small { color: var(--nc-text-secondary); }
.topology-scope-card p { margin: 4px 0 0; color: var(--nc-text-secondary); font-size: 11px; }
.topology-scope-card .card__body { gap: 12px; }
</style>
