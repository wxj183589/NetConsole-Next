<script setup lang="ts">
import { computed, onActivated, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { CircleCheck, Document, Expand, Filter, Fold, Folder, Monitor, Search, Upload, Warning } from '@element-plus/icons-vue'
import NcLayout from '../components/NcLayout.vue'
import {
  ApiRequestError, cancelDeviceBatch, commitDeviceImport, confirmDeviceBatch, createDeviceBatch, createDeviceGroup,
  getDeviceBatchOperation, getDeviceGroups, getDeviceInventory, getDeviceInventoryDetail, previewDeviceImport, updateDevice,
  type DeviceBatchKind, type DeviceBatchOperation, type DeviceBatchRequest, type DeviceGroup, type DeviceImportPreview, type DeviceInventoryDetail,
  type DeviceInventoryItem, type DeviceInventoryPage,
} from '../api/client'
import { useAuthStore } from '../stores/auth'
import { useSiteContextStore } from '../stores/siteContext'

const route = useRoute(); const router = useRouter(); const auth = useAuthStore(); const siteContext = useSiteContextStore()
const result = ref<DeviceInventoryPage | null>(null); const groups = ref<DeviceGroup[]>([])
const loading = ref(false); const unsupported = ref(false); const page = ref(1); const pageSize = ref(20)
const ALL_SITES = '__ALL_SITES__'
const search = ref(String(route.query.q || '')); const status = ref(''); const viewSiteId = ref(siteContext.currentSiteId); const siteId = computed(() => viewSiteId.value === ALL_SITES ? undefined : viewSiteId.value || undefined); const groupId = ref(''); const ungrouped = ref(false); const vendor = ref(''); const deviceType = ref('')
const organizationSearch = ref(''); const treeCollapsed = ref(false); const ungroupedCount = ref(0)
let inventoryRefreshVersion = 0; let groupRefreshVersion = 0
const showDetail = ref(false); const showGroup = ref(false); const showImport = ref(false)
const selected = ref<DeviceInventoryDetail | null>(null); const importPreview = ref<DeviceImportPreview | null>(null); const importCsv = ref('')
const selectedIds = ref<string[]>([]); const showBatchDialog = ref(false); const showBatchPreview = ref(false)
const batchOperation = ref<DeviceBatchOperation | null>(null); const batchLoading = ref(false); const operationId = ref('')
const batchKind = ref<DeviceBatchKind>('BATCH_ENABLE'); const batchSite = ref(''); const batchGroup = ref('')
const batchMetadata = reactive({ name: '', vendor: '', model: '', deviceType: '', description: '' })
const groupForm = reactive({ siteId: '', name: '', description: '' })
const canWrite = computed(() => auth.user?.role === 'SUPERADMIN')
const rows = computed(() => result.value?.items || [])
const allRowsSelected = computed(() => rows.value.length > 0 && rows.value.every(row => selectedIds.value.includes(row.id)))
const displayBatchKind = computed(() => batchOperation.value?.kind || batchKind.value)
const batchKindLabels = { BATCH_ENABLE: '批量启用', BATCH_DISABLE: '批量禁用', BATCH_SET_GROUP: '批量设置分组', BATCH_SET_SITE: '批量设置局点', BATCH_UPDATE_METADATA: '批量更新元数据' } as Record<DeviceBatchKind, string>
const batchKindLabel = computed(() => batchKindLabels[displayBatchKind.value])
const batchDialogKindLabel = computed(() => batchKindLabels[batchKind.value])
const operationStateLabel = computed(() => ({ PENDING_CONFIRM: '待确认', RUNNING: '执行中', SUCCEEDED: '已完成', FAILED: '失败可重试', CONFLICT: '发生冲突', CANCELLED: '已取消', INTERRUPTED: '服务重启中断', PREVIEWED: '待确认' } as Record<string, string>)[batchOperation.value?.state || ''] || batchOperation.value?.state || '—')
const summary = computed(() => result.value?.summary || { totalRecords: 0, validDevices: 0, invalidRecords: 0, enabledDevices: 0, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 0 })
const visibleGroups = computed(() => {
  const needle = organizationSearch.value.trim().toLowerCase()
  return groups.value.filter(group => !needle || `${group.name} ${group.siteId}`.toLowerCase().includes(needle))
})
const currentOrganizationLabel = computed(() => {
  const siteLabel = viewSiteId.value === ALL_SITES ? '全部授权 Site' : siteContext.activeSites.find(site => site.id === viewSiteId.value)?.name || '当前 Site'
  if (ungrouped.value) return `${siteLabel} · 未分组`
  if (groupId.value) return `${siteLabel} · ${groups.value.find(group => group.id === groupId.value)?.name || '设备分组'}`
  return `${siteLabel} · 全部设备`
})

async function refresh(): Promise<void> {
  const refreshVersion = ++inventoryRefreshVersion
  loading.value = true; unsupported.value = false
  try {
    const nextResult = await getDeviceInventory({ page: page.value, pageSize: pageSize.value, search: search.value, status: status.value, siteId: siteId.value, groupId: groupId.value, ungrouped: ungrouped.value, vendor: vendor.value, deviceType: deviceType.value })
    if (refreshVersion === inventoryRefreshVersion) result.value = nextResult
  }
  catch (cause) {
    if (refreshVersion !== inventoryRefreshVersion) return
    unsupported.value = cause instanceof ApiRequestError && [403, 404, 503].includes(cause.status)
    if (!unsupported.value) ElMessage.error('设备库存 API 读取失败')
  }
  finally { if (refreshVersion === inventoryRefreshVersion) loading.value = false }
}
async function refreshGroups(forSiteId: string | undefined = siteId.value): Promise<void> {
  const refreshVersion = ++groupRefreshVersion
  try {
    const [nextGroups, ungroupedPage] = await Promise.all([
      getDeviceGroups(forSiteId),
      getDeviceInventory({ page: 1, pageSize: 1, siteId: forSiteId, ungrouped: true }),
    ])
    if (refreshVersion !== groupRefreshVersion) return
    groups.value = nextGroups; ungroupedCount.value = ungroupedPage.total
  } catch { if (refreshVersion === groupRefreshVersion) ElMessage.error('设备组织树读取失败') }
}
function applyFilters(): void { page.value = 1; void refresh() }
function clearFilters(): void { search.value = ''; status.value = ''; groupId.value = ''; ungrouped.value = false; vendor.value = ''; deviceType.value = ''; applyFilters() }
function selectOrganization(nextGroupId = '', nextUngrouped = false): void { groupId.value = nextGroupId; ungrouped.value = nextUngrouped; selectedIds.value = []; page.value = 1; void refresh() }
function selectSite(nextSiteId: string): void { viewSiteId.value = nextSiteId; selectedIds.value = []; groupId.value = ''; ungrouped.value = false; page.value = 1 }
function onSelectionChange(id: string, event: Event): void { const checked = (event.target as HTMLInputElement).checked; selectedIds.value = checked ? Array.from(new Set([...selectedIds.value, id])) : selectedIds.value.filter(item => item !== id) }
function togglePageSelection(): void { selectedIds.value = allRowsSelected.value ? selectedIds.value.filter(id => !rows.value.some(row => row.id === id)) : Array.from(new Set([...selectedIds.value, ...rows.value.map(row => row.id)])) }
function clearSelection(): void { selectedIds.value = [] }
function newIdempotencyKey(): string { return globalThis.crypto?.randomUUID?.() || `batch-${Date.now()}-${Math.random().toString(16).slice(2)}` }
function openBatch(kind: DeviceBatchKind): void { if (!canWrite.value || !selectedIds.value.length) return; batchKind.value = kind; batchSite.value = siteId.value || siteContext.currentSiteId; batchGroup.value = ''; Object.assign(batchMetadata, { name: '', vendor: '', model: '', deviceType: '', description: '' }); operationId.value = newIdempotencyKey(); showBatchDialog.value = true }
function batchPayload(): DeviceBatchRequest {
  const payload: DeviceBatchRequest = { kind: batchKind.value, idempotencyKey: operationId.value, deviceIds: [...selectedIds.value] }
  if (batchKind.value === 'BATCH_SET_SITE') payload.siteId = batchSite.value
  if (batchKind.value === 'BATCH_SET_GROUP') payload.groupId = batchGroup.value
  if (batchKind.value === 'BATCH_UPDATE_METADATA') Object.assign(payload, Object.fromEntries(Object.entries(batchMetadata).filter(([, value]) => value.trim() !== '')))
  return payload
}
async function createBatchPreview(): Promise<void> { batchLoading.value = true; try { batchOperation.value = await createDeviceBatch(batchPayload()); sessionStorage.setItem('netconsole.deviceBatchOperationId', batchOperation.value.id); showBatchDialog.value = false; showBatchPreview.value = true; ElMessage.success('批量预览已生成，请明确确认后提交') } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '批量预览失败') } finally { batchLoading.value = false } }
async function reloadBatch(): Promise<void> { if (!batchOperation.value?.id && !operationId.value) return; batchLoading.value = true; try { batchOperation.value = await getDeviceBatchOperation(batchOperation.value?.id || operationId.value); operationId.value = batchOperation.value.id } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '批量状态读取失败') } finally { batchLoading.value = false } }
async function confirmBatch(): Promise<void> { if (!batchOperation.value?.canConfirm) return; batchLoading.value = true; try { batchOperation.value = await confirmDeviceBatch(batchOperation.value.id); ElMessage.success('批量操作已原子提交'); selectedIds.value = []; await refresh() } catch (cause) { if (cause instanceof ApiRequestError && cause.status === 409) { await reloadBatch(); ElMessage.warning('设备版本或范围发生变化，批量操作未写入，请重新预览') } else ElMessage.error(cause instanceof Error ? cause.message : '批量提交失败') } finally { batchLoading.value = false } }
async function cancelBatch(): Promise<void> { if (!batchOperation.value?.canCancel) return; batchLoading.value = true; try { batchOperation.value = await cancelDeviceBatch(batchOperation.value.id); ElMessage.info('批量操作已取消') } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '批量取消失败') } finally { batchLoading.value = false } }
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function statusLabel(value: string): string { return ({ CONNECTED: '最近在线', DISCONNECTED: '最近离线', FAILED: '会话失败', CLOSED: '会话关闭', DISABLED: '已禁用', UNKNOWN: '状态未知' } as Record<string, string>)[value] || '未知' }
function statusClass(value: string): string { return value === 'CONNECTED' ? 'good' : value === 'DISABLED' || value === 'FAILED' ? 'bad' : value === 'UNKNOWN' ? 'neutral' : 'warn' }
function openCreate(): void { void router.push('/devices/new') }
function openEdit(row: DeviceInventoryItem): void { void router.push(`/devices/${encodeURIComponent(row.id)}/edit`) }
async function openDetails(row: DeviceInventoryItem): Promise<void> { try { selected.value = await getDeviceInventoryDetail(row.id); showDetail.value = true } catch { ElMessage.error('设备详情读取失败') } }
async function toggle(row: DeviceInventoryItem): Promise<void> {
  if (!canWrite.value) return
  try { const detail = await getDeviceInventoryDetail(row.id); const item = detail.item; await updateDevice(row.id, { id: row.id, name: item.name, managementIp: item.managementIp || item.managementAddress, vendor: item.vendor, model: item.model, deviceType: item.deviceType, siteId: item.siteId || siteId.value, groupId: item.groupId || null, description: detail.description, enabled: !item.enabled, terminalProfileId: item.terminalProfileId || null }); ElMessage.success(item.enabled ? '设备已禁用' : '设备已启用'); await refresh() }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备状态更新失败') }
}
function openInner(row: DeviceInventoryItem): void { if (row.terminalProfileId) router.push({ path: '/terminal', query: { deviceId: row.terminalProfileId } }); else ElMessage.warning('该设备尚未关联终端档案') }
async function createGroup(): Promise<void> { try { await createDeviceGroup(groupForm); ElMessage.success('分组已创建'); showGroup.value = false; groupForm.name = ''; groupForm.description = ''; await refreshGroups() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '分组创建失败') } }
function onFile(event: Event): void { const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return; const reader = new FileReader(); reader.onload = () => { importCsv.value = String(reader.result || ''); void previewImport() }; reader.readAsText(file, 'UTF-8') }
async function previewImport(): Promise<void> { try { importPreview.value = await previewDeviceImport(importCsv.value); showImport.value = true } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : 'CSV 预览失败') } }
async function commitImport(): Promise<void> { if (!importPreview.value || importPreview.value.conflictCount || importPreview.value.errorCount) return; try { const output = await commitDeviceImport(importPreview.value.previewId); ElMessage.success(`导入完成：新增 ${output.created}，更新 ${output.updated}`); showImport.value = false; importPreview.value = null; await refresh() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '导入提交失败') } }
function importFile(): void { document.getElementById('device-import-file')?.click() }
watch(() => siteContext.currentSiteId, value => { if (value && viewSiteId.value !== ALL_SITES) { viewSiteId.value = value; groupForm.siteId = value } })
watch(viewSiteId, async value => { const nextSiteId = value === ALL_SITES ? undefined : value || undefined; groupForm.siteId = value === ALL_SITES ? siteContext.currentSiteId : value; await Promise.all([refresh(), refreshGroups(nextSiteId)]) })
onMounted(async () => { groupForm.siteId = siteContext.currentSiteId; const saved = sessionStorage.getItem('netconsole.deviceBatchOperationId'); if (saved) { operationId.value = saved; try { batchOperation.value = await getDeviceBatchOperation(saved); showBatchPreview.value = true } catch { sessionStorage.removeItem('netconsole.deviceBatchOperationId') } } await Promise.all([refresh(), refreshGroups()]) })
onActivated(() => { if (result.value) void Promise.all([refresh(), refreshGroups()]) })
</script>

<template>
  <NcLayout title="设备管理" description="设备库存与终端连接档案分离；所有写操作经服务端权限、CSRF 与审计校验">
    <template #actions>
      <el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button size="small" @click="showGroup = true">分组管理</el-button><el-button size="small" @click="importFile"><el-icon><Upload /></el-icon>CSV 导入</el-button><el-button v-if="canWrite" type="primary" size="small" @click="openCreate">新增设备</el-button><input id="device-import-file" type="file" accept=".csv,text/csv" hidden @change="onFile" />
    </template>
    <div v-if="unsupported" class="card"><div class="empty-state"><div><el-icon><Monitor /></el-icon><strong>设备库存 API 尚未启用</strong><small>当前服务未提供设备查询能力；页面不会使用前端模拟数据。</small></div></div></div>
    <template v-else>
      <div class="device-workspace" :class="{ 'is-collapsed': treeCollapsed }">
        <aside class="device-tree-panel" aria-label="设备组织导航">
          <header class="device-tree-header">
            <div><span class="eyebrow">组织导航</span><h2>设备组织</h2></div>
            <button class="device-tree-toggle" type="button" :aria-expanded="!treeCollapsed" :title="treeCollapsed ? '展开组织导航' : '折叠组织导航'" @click="treeCollapsed = !treeCollapsed"><el-icon><Expand v-if="treeCollapsed" /><Fold v-else /></el-icon><span class="sr-only">{{ treeCollapsed ? '展开组织导航' : '折叠组织导航' }}</span></button>
          </header>
          <template v-if="!treeCollapsed">
            <label class="device-tree-search"><el-icon><Search /></el-icon><input v-model="organizationSearch" aria-label="搜索 Site 或分组" placeholder="搜索 Site / 分组" /></label>
            <div class="device-tree-section">
              <button class="device-tree-node" :class="{ active: viewSiteId === ALL_SITES }" type="button" @click="selectSite(ALL_SITES)"><span><el-icon><Folder /></el-icon>全部授权 Site</span><b>{{ summary.totalRecords }}</b></button>
              <button class="device-tree-node" :class="{ active: viewSiteId !== ALL_SITES && !groupId && !ungrouped }" type="button" @click="selectOrganization()"><span><el-icon><Monitor /></el-icon>全部设备</span><b>{{ summary.totalRecords }}</b></button>
            </div>
            <div class="device-tree-section">
              <div class="device-tree-label">Site / 分组</div>
              <button v-for="site in siteContext.activeSites" :key="site.id" class="device-tree-node" :class="{ active: viewSiteId === site.id && !groupId && !ungrouped }" type="button" @click="selectSite(site.id)"><span><span class="tree-caret">›</span>{{ site.name }}</span><small>{{ site.id }}</small></button>
              <button v-for="group in visibleGroups" :key="group.id" class="device-tree-node device-tree-node--child" :class="{ active: groupId === group.id }" type="button" @click="selectOrganization(group.id)"><span><span class="tree-indent">↳</span>{{ group.name }}<small v-if="viewSiteId === ALL_SITES">{{ group.siteId }}</small></span><b>{{ group.deviceCount }}</b></button>
              <button class="device-tree-node device-tree-node--child" :class="{ active: ungrouped }" type="button" @click="selectOrganization('', true)"><span><span class="tree-indent">↳</span>未分组</span><b>{{ ungroupedCount }}</b></button>
            </div>
            <p class="device-tree-hint">当前范围：{{ currentOrganizationLabel }}；跨 Site 修改仍由服务端验证。</p>
          </template>
        </aside>
        <section class="device-list-workspace">
          <div class="workspace-heading"><div><span class="eyebrow">设备库存</span><h2>{{ currentOrganizationLabel }}</h2></div><span class="muted">服务端分页 · 当前结果 {{ result?.total || 0 }} 条</span></div>
      <div class="inventory-summary"><span class="summary-chip"><b>{{ summary.totalRecords }}</b> 条设备</span><span class="summary-chip summary-chip--success"><b>{{ summary.enabledDevices }}</b> 已启用</span><span class="summary-chip"><b>{{ summary.disabledDevices }}</b> 已禁用</span><span class="summary-chip summary-chip--info"><b>{{ summary.onlineDevices }}</b> 最近在线</span><span class="summary-chip"><b>{{ summary.unknownStatus }}</b> 状态未知</span></div>
      <div v-if="summary.invalidRecords" class="data-quality-note"><el-icon><Warning /></el-icon>服务端报告 {{ summary.invalidRecords }} 条结构异常记录，异常不被隐藏。</div>
      <section class="card">
        <div class="filter-bar"><label class="device-filter-search filter-input"><el-icon><Search /></el-icon><input v-model="search" placeholder="搜索名称、设备 ID、管理 IP、厂商、类型" @keydown.enter="applyFilters" /></label><select v-model="vendor" aria-label="设备厂商" @change="applyFilters"><option value="">全部厂商</option><option value="H3C">H3C</option><option value="ZTE">ZTE</option><option value="Cisco">Cisco</option><option value="Ruijie">Ruijie</option></select><select v-model="deviceType" aria-label="设备类型" @change="applyFilters"><option value="">全部类型</option><option value="H3C_SWITCH">H3C 交换机</option><option value="H3C_WIRELESS_CONTROLLER">H3C 无线控制器</option><option value="H3C_WIRELESS_AP">H3C 无线 AP</option><option value="H3C_ROUTER">H3C 路由器</option><option value="NETWORK_DEVICE">通用网络设备</option><option value="AP">AP</option></select><select v-model="groupId" aria-label="设备分组" @change="ungrouped = false; applyFilters()"><option value="">全部分组</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select><select v-model="status" aria-label="设备状态" @change="applyFilters"><option value="">全部状态</option><option value="ENABLED">已启用</option><option value="DISABLED">已禁用</option><option value="ONLINE">最近在线</option><option value="OFFLINE">最近离线</option><option value="UNKNOWN">状态未知</option></select><button class="icon-button" type="button" title="清除筛选" @click="clearFilters"><el-icon><Filter /></el-icon></button><span class="filter-bar__grow" /><span class="muted">服务端分页 · 最新更新时间排序</span></div>
        <div v-if="canWrite" class="batch-toolbar"><label class="batch-select-all"><input type="checkbox" :checked="allRowsSelected" aria-label="批量当前页全选" @change="togglePageSelection" />当前页全选</label><span class="muted">已选择 {{ selectedIds.length }} 台（跨页选择会保留明确设备 ID）</span><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_ENABLE')">批量启用</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_DISABLE')">批量禁用</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_SET_GROUP')">设置分组</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_SET_SITE')">设置局点</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_UPDATE_METADATA')">更新元数据</el-button><el-button v-if="selectedIds.length" text size="small" @click="clearSelection">清除选择</el-button></div>
        <div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th><input type="checkbox" :checked="allRowsSelected" aria-label="批量当前页全选" @change="togglePageSelection" /></th><th>状态</th><th>设备名称 / ID</th><th>管理 IP</th><th>厂商 / 型号</th><th>类型</th><th>局点 / 分组</th><th>终端档案</th><th>最近连通</th><th>更新时间</th><th>操作</th></tr></thead><tbody><tr v-for="device in rows" :key="device.id" :class="{ 'is-invalid': !device.isValid }" @dblclick="openDetails(device)"><td><input type="checkbox" :checked="selectedIds.includes(device.id)" :aria-label="`选择设备 ${device.name}`" @change="onSelectionChange(device.id, $event)" /></td><td><span :class="['state-pill', statusClass(device.connectionStatus)]"><i class="status-dot" />{{ statusLabel(device.connectionStatus) }}</span></td><td><span class="device-name">{{ device.name }}</span><span class="device-subline mono">{{ device.deviceId || device.id }}<span v-if="!device.isValid" class="device-quality">· {{ device.qualityIssues.join(' / ') }}</span></span></td><td class="device-address">{{ device.managementIp || device.managementAddress }}</td><td>{{ device.vendor }}<span class="device-subline">{{ device.model }}</span></td><td>{{ device.deviceType }}</td><td>{{ device.site }}<span class="device-subline">{{ device.group }}</span></td><td><span :class="['state-pill', device.terminalProfileId ? 'good' : 'neutral']">{{ device.terminalProfileId ? '已关联' : '未关联' }}</span></td><td>{{ formatTime(device.lastSeenMs) }}</td><td>{{ formatTime(device.updatedAt) }}</td><td><div class="table-actions"><el-button text size="small" @click="openDetails(device)">详情</el-button><el-button v-if="canWrite" text size="small" @click="openEdit(device)">编辑</el-button><el-button v-if="canWrite" text size="small" @click="toggle(device)">{{ device.enabled ? '禁用' : '启用' }}</el-button><el-button text size="small" :disabled="!device.terminalProfileId" @click="openInner(device)">终端</el-button></div></td></tr><tr v-if="!rows.length"><td colspan="11"><div class="empty-state"><div><el-icon><Document /></el-icon><strong>{{ search || status || groupId || ungrouped || vendor || deviceType ? '没有符合条件的设备' : '暂无设备' }}</strong><small>新增设备或导入 CSV 后，数据会从 Catalog 真实读取。</small></div></div></td></tr></tbody></table></div>
        <div class="pagination-bar"><span>第 {{ page }} 页 · 每页 {{ pageSize }} 条 · 共 {{ result?.total || 0 }} 条</span><div class="toolbar"><select v-model.number="pageSize" @change="page = 1; refresh()"><option :value="20">20 / 页</option><option :value="50">50 / 页</option><option :value="100">100 / 页</option></select><el-button size="small" :disabled="page <= 1" @click="page--; refresh()">上一页</el-button><el-button size="small" :disabled="!result?.hasMore" @click="page++; refresh()">下一页</el-button></div></div>
      </section>
        </section>
      </div>
    </template>
    <div class="section-label">数据与安全口径</div><div class="card card--flat"><div class="card__body overview-footer"><span><el-icon><CircleCheck /></el-icon>设备库存与 h3c_device_profiles 分离，终端历史按原 profile ID 保留</span><span><el-icon><CircleCheck /></el-icon>启用状态由服务端同步到已关联终端档案</span><span><el-icon><Warning /></el-icon>CSV 不接受密码、凭据或私钥列，导入先预览再原子提交</span></div></div>
    <el-dialog v-model="showDetail" title="设备详情 / 审计" width="760px"><template v-if="selected"><div class="detail-grid"><div><span>设备名称</span><b>{{ selected.item.name }}</b></div><div><span>设备 ID</span><b class="mono">{{ selected.item.id }}</b></div><div><span>管理 IP</span><b class="mono">{{ selected.item.managementIp || selected.item.managementAddress }}</b></div><div><span>厂商 / 型号</span><b>{{ selected.item.vendor }} / {{ selected.item.model }}</b></div><div><span>局点 / 分组</span><b>{{ selected.item.site }} / {{ selected.item.group }}</b></div><div><span>终端档案</span><b>{{ selected.terminalProfileId || '未关联' }}</b></div><div><span>创建时间</span><b>{{ formatTime(selected.createdMs) }}</b></div><div><span>更新时间</span><b>{{ formatTime(selected.updatedMs) }}</b></div></div><p class="muted">{{ selected.description || '无描述' }}</p><div v-if="selected.item.qualityIssues.length" class="data-quality-note"><el-icon><Warning /></el-icon>{{ selected.item.qualityIssues.join(' / ') }}</div><h3>最近终端会话</h3><div v-if="!selected.recentSessions.length" class="muted">暂无持久化终端历史</div><div v-for="session in selected.recentSessions" :key="session.id" class="overview-footer"><span>{{ formatTime(session.startedMs) }} · {{ session.protocol }}</span><span>{{ session.state }} · {{ session.recordingStatus }}</span></div><h3>审计记录</h3><div v-for="audit in selected.auditEvents" :key="audit.id" class="overview-footer"><span>{{ formatTime(audit.atMs) }} · {{ audit.action }}</span><span>{{ audit.outcome }}</span></div></template></el-dialog>
    <el-dialog v-model="showGroup" title="设备分组管理" width="520px"><div class="form-grid"><label class="form-field"><span>当前局点</span><input v-model="groupForm.siteId" disabled /></label><label class="form-field"><span>分组名称</span><input v-model="groupForm.name" /></label><label class="form-field"><span>描述</span><input v-model="groupForm.description" /></label></div><div class="overview-footer" style="margin-top:16px"><span v-for="group in groups" :key="group.id">{{ group.name }} · {{ group.deviceCount }} 台</span></div><template #footer><el-button @click="showGroup = false">关闭</el-button><el-button v-if="canWrite" type="primary" @click="createGroup">新增分组</el-button></template></el-dialog>
    <el-dialog v-model="showImport" title="CSV 导入预览" width="820px"><p class="muted">预览不会写入数据库；只有无冲突、明确确认后才会执行单事务提交。支持 create / update，任何错误都会阻止整批写入。</p><div v-if="importPreview" class="inventory-summary"><span class="summary-chip"><b>{{ importPreview.totalRows }}</b> 行</span><span class="summary-chip summary-chip--success"><b>{{ importPreview.createCount }}</b> 新增</span><span class="summary-chip summary-chip--info"><b>{{ importPreview.updateCount }}</b> 更新</span><span class="summary-chip summary-chip--danger"><b>{{ importPreview.conflictCount + importPreview.errorCount }}</b> 冲突/错误</span></div><div class="table-wrap"><table class="data-table"><thead><tr><th>行</th><th>动作</th><th>名称</th><th>管理 IP</th><th>说明</th></tr></thead><tbody><tr v-for="row in importPreview?.rows || []" :key="row.line"><td>{{ row.line }}</td><td>{{ row.action }}</td><td>{{ row.name }}</td><td>{{ row.managementIp }}</td><td>{{ row.detail }}</td></tr></tbody></table></div><template #footer><el-button @click="showImport = false">取消</el-button><el-button type="primary" :disabled="!importPreview || !!(importPreview.conflictCount || importPreview.errorCount)" @click="commitImport">确认原子导入</el-button></template></el-dialog>
    <el-dialog v-model="showBatchDialog" :title="batchDialogKindLabel" width="620px"><p class="muted">本次操作只提交明确选择的 {{ selectedIds.length }} 台设备。服务端会记录版本并在确认时再次校验。</p><div class="form-grid"><label v-if="batchKind === 'BATCH_SET_SITE'" class="form-field"><span>目标局点</span><select v-model="batchSite" required><option v-for="site in siteContext.activeSites" :key="site.id" :value="site.id">{{ site.name }} · {{ site.id }}</option></select></label><label v-if="batchKind === 'BATCH_SET_GROUP'" class="form-field"><span>目标分组</span><select v-model="batchGroup"><option value="">请选择分组</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }} · {{ group.siteId }}</option></select></label><template v-if="batchKind === 'BATCH_UPDATE_METADATA'"><label class="form-field"><span>名称（留空保持）</span><input v-model="batchMetadata.name" /></label><label class="form-field"><span>厂商（留空保持）</span><input v-model="batchMetadata.vendor" /></label><label class="form-field"><span>型号（留空保持）</span><input v-model="batchMetadata.model" /></label><label class="form-field"><span>设备类型（留空保持）</span><input v-model="batchMetadata.deviceType" /></label><label class="form-field"><span>描述（留空保持）</span><input v-model="batchMetadata.description" /></label></template></div><template #footer><el-button @click="showBatchDialog = false">取消</el-button><el-button type="primary" :loading="batchLoading" :disabled="(batchKind === 'BATCH_SET_GROUP' && !batchGroup) || (batchKind === 'BATCH_SET_SITE' && !batchSite)" @click="createBatchPreview">生成预览</el-button></template></el-dialog>
    <el-dialog v-model="showBatchPreview" title="批量操作预览" width="900px"><template v-if="batchOperation"><div class="inventory-summary"><span class="summary-chip"><b>{{ batchKindLabel }}</b></span><span class="summary-chip"><b>{{ batchOperation.targetCount }}</b> 台目标</span><span class="summary-chip" :class="batchOperation.state === 'CONFLICT' ? 'summary-chip--danger' : 'summary-chip--success'"><b>{{ operationStateLabel }}</b></span><span class="summary-chip">已完成 {{ batchOperation.completedCount }} · 冲突 {{ batchOperation.conflictCount }} · 失败 {{ batchOperation.failedCount }}</span></div><p class="muted">操作 ID：<span class="mono">{{ batchOperation.id }}</span> · 幂等键与操作状态已保存，刷新页面可恢复。</p><div class="table-wrap"><table class="data-table"><thead><tr><th>设备 ID</th><th>预期版本</th><th>预览结果</th><th>说明</th></tr></thead><tbody><tr v-for="item in batchOperation.items" :key="item.deviceId"><td class="mono">{{ item.deviceId }}</td><td>{{ item.expectedRevision }}</td><td>{{ item.outcome }}</td><td>{{ item.detail }}</td></tr></tbody></table></div><p v-if="batchOperation.state === 'CONFLICT'" class="data-quality-note"><el-icon><Warning /></el-icon>预览发现冲突，服务端不会允许确认；请刷新设备数据后重新选择并生成新预览。</p></template><template #footer><el-button :loading="batchLoading" @click="reloadBatch">重新加载状态</el-button><el-button v-if="batchOperation?.canCancel" :loading="batchLoading" @click="cancelBatch">取消操作</el-button><el-button v-if="batchOperation?.canConfirm" type="primary" :loading="batchLoading" @click="confirmBatch">{{ batchOperation?.canRetry ? '安全重试' : '确认批量提交' }}</el-button><el-button @click="showBatchPreview = false">关闭</el-button></template></el-dialog>
  </NcLayout>
</template>
