<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { CircleCheck, Document, Filter, Monitor, Search, Upload, Warning } from '@element-plus/icons-vue'
import NcLayout from '../components/NcLayout.vue'
import DeviceLldpNeighbors from '../components/DeviceLldpNeighbors.vue'
import {
  ApiRequestError, cancelDeviceBatch, commitDeviceImport, confirmDeviceBatch, createDevice, createDeviceBatch, createDeviceGroup,
  getDeviceBatchOperation, getDeviceGroups, getDeviceInventory, getDeviceInventoryDetail, getDeviceSites, getModuleStates, previewDeviceImport, updateDevice,
  cancelH3cTaskPlan, createDeviceConnectionTests, createH3cProfile, getH3cTaskPlanReport, getH3cOperationalCapabilities, onboardH3cProfile, testH3cConnection,
  type DeviceBatchKind, type DeviceBatchOperation, type DeviceBatchRequest, type DeviceGroup, type DeviceImportPreview, type DeviceInventoryDetail,
  type DeviceInventoryItem, type DeviceInventoryPage, type DeviceSite, type H3cTaskPlan, type H3cTaskPlanReport, type H3cOperationalCapabilityView,
} from '../api/client'
import { useAuthStore } from '../stores/auth'
import { canCancelTask, taskFailureLabel, taskStateLabel } from '../taskViewModel'

const route = useRoute(); const router = useRouter(); const auth = useAuthStore()
const result = ref<DeviceInventoryPage | null>(null); const sites = ref<DeviceSite[]>([]); const groups = ref<DeviceGroup[]>([])
const loading = ref(false); const saving = ref(false); const unsupported = ref(false); const page = ref(1); const pageSize = ref(20)
const search = ref(String(route.query.q || '')); const status = ref(''); const vendor = ref(''); const deviceType = ref(''); const siteId = ref('local'); const groupId = ref('')
const showEditor = ref(false); const showDetail = ref(false); const showGroup = ref(false); const showImport = ref(false)
const detailError = ref('')
const showOnboard = ref(false)
const selected = ref<DeviceInventoryDetail | null>(null); const importPreview = ref<DeviceImportPreview | null>(null); const importCsv = ref('')
const operationalCapabilities = ref<H3cOperationalCapabilityView | null>(null)
const capabilityLoading = ref(false); const capabilityError = ref(''); const capabilityUnavailable = ref('')
const capabilityLabels: Record<string, string> = { DEVICE_IDENTITY: '设备身份', AC_ROLE_READ: '控制器角色读取', INTERFACE_READ: '接口读取', VLAN_READ: 'VLAN 读取', LLDP_READ: 'LLDP 读取', AP_INVENTORY_READ: 'AP 清单', AP_ADDRESS_READ: 'AP 地址', AP_RADIO_READ: 'AP 射频', AP_DETAILS_READ: 'AP 详情', CPU_READ: 'CPU 读取', MEMORY_READ: '内存读取', HA_SWITCH: 'HA 切换', WHOLE_CONFIGURATION_RESTORE: '整机配置恢复' }
const backupModuleState = ref<'unknown' | 'enabled' | 'disabled'>('unknown')
const backupUnavailableReason = computed(() => {
  const detail = selected.value
  if (!detail || detail.item.vendor.trim().toLowerCase() !== 'h3c' || detail.item.deviceType.toUpperCase() === 'FIT_AP') return '当前仅支持独立管理的 H3C 设备'
  if (!detail.terminalProfileId || detail.terminalProfile?.id !== detail.terminalProfileId || (detail.item.terminalProfileId && detail.item.terminalProfileId !== detail.terminalProfileId)) return '该设备尚未配置连接'
  return backupModuleState.value === 'enabled' ? '' : backupModuleState.value === 'disabled' ? '配置备份模块已停用' : '配置备份模块状态暂不可用'
})
const connectionTesting = ref(false); const connectionResult = ref<{ status: string; message: string; protocol: string; vendor?: string | null; version?: string | null; elapsedMs: number } | null>(null)
const selectedIds = ref<string[]>([]); const showBatchDialog = ref(false); const showBatchPreview = ref(false)
type ConnectionTarget = { id: string; name: string; profileId?: string | null }
const selectedTargets = new Map<string, ConnectionTarget>()
const connectionPlan = ref<H3cTaskPlan | null>(null); const connectionReport = ref<H3cTaskPlanReport | null>(null)
const connectionPlanBusy = ref(false); const connectionPlanMessage = ref(''); const connectionTargets = ref<ConnectionTarget[]>([])
const batchOperation = ref<DeviceBatchOperation | null>(null); const batchLoading = ref(false); const operationId = ref('')
const batchKind = ref<DeviceBatchKind>('BATCH_ENABLE'); const batchSite = ref('local'); const batchGroup = ref('')
const batchMetadata = reactive({ name: '', vendor: '', model: '', deviceType: '', description: '' })
const form = reactive({ id: '', name: '', managementIp: '', protocol: 'SSH' as 'SSH' | 'TELNET' | 'AUTO', port: 22, username: '', password: '', vendor: '', model: '', deviceType: 'NETWORK_DEVICE', siteId: 'local', groupId: '', description: '', enabled: true, terminalProfileId: '' })
const onboardForm = reactive({ host: '', protocol: 'SSH' as 'SSH' | 'TELNET' | 'AUTO', port: 22, username: '', password: '', allowTelnetFallback: false, vendor: 'H3C', platform: '', name: '', description: '' })
const groupForm = reactive({ siteId: 'local', name: '', description: '' })
const canWrite = computed(() => auth.user?.role === 'SUPERADMIN')
const connectionPlanRunning = computed(() => Boolean(connectionPlan.value && canCancelTask(connectionPlan.value.state)))
const connectionPlanCompleted = computed(() => connectionPlan.value?.items.filter(item => !canCancelTask(item.state)).length || 0)
const connectionPlanRows = computed(() => connectionPlan.value?.items.map(item => ({
  item, target: connectionTargets.value.find(target => target.profileId === item.target),
  report: connectionReport.value?.items.find(report => report.item.ordinal === item.ordinal),
})) || [])
const rows = computed(() => result.value?.items || [])
const siteScopeLabel = computed(() => siteId.value ? sites.value.find(site => site.id === siteId.value)?.name || rows.value.find(row => row.siteId === siteId.value)?.site || siteId.value : '全部已授权局点')
const selectedGroup = computed(() => groups.value.find(group => group.id === groupId.value))
const vendorOptions = computed(() => Array.from(new Set(['H3C', 'ZTE', ...rows.value.map(row => row.vendor).filter(value => value && value !== '—')])).sort())
const deviceTypeOptions = computed(() => Array.from(new Set(['NETWORK_DEVICE', 'SWITCH', 'ROUTER', 'AC', 'FAT_AP', ...rows.value.map(row => row.deviceType).filter(Boolean)])).sort())
const editableDeviceTypeOptions = computed(() => deviceTypeOptions.value.filter(type => type.toUpperCase() !== 'FIT_AP'))
const hasFilters = computed(() => Boolean(search.value || status.value || vendor.value || deviceType.value || groupId.value))
const allRowsSelected = computed(() => rows.value.length > 0 && rows.value.every(row => selectedIds.value.includes(row.id)))
const displayBatchKind = computed(() => batchOperation.value?.kind || batchKind.value)
const batchKindLabels = { BATCH_ENABLE: '批量启用', BATCH_DISABLE: '批量禁用', BATCH_SET_GROUP: '批量设置分组', BATCH_SET_SITE: '批量设置局点', BATCH_UPDATE_METADATA: '批量更新元数据' } as Record<DeviceBatchKind, string>
const batchKindLabel = computed(() => batchKindLabels[displayBatchKind.value])
const batchDialogKindLabel = computed(() => batchKindLabels[batchKind.value])
const operationStateLabel = computed(() => ({ PENDING_CONFIRM: '待确认', RUNNING: '执行中', SUCCEEDED: '已完成', FAILED: '失败可重试', CONFLICT: '发生冲突', CANCELLED: '已取消', INTERRUPTED: '服务重启中断', PREVIEWED: '待确认' } as Record<string, string>)[batchOperation.value?.state || ''] || batchOperation.value?.state || '—')
const summary = computed(() => result.value?.summary || { totalRecords: 0, validDevices: 0, invalidRecords: 0, enabledDevices: 0, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 0 })
let refreshSequence = 0; let groupSequence = 0; let detailSequence = 0
let connectionContext = 0; let connectionActive = true; let connectionPollCount = 0
let detailsReady = false
let connectionPoll: ReturnType<typeof setTimeout> | undefined

function clearCapabilityState(): void { operationalCapabilities.value = null; capabilityLoading.value = false; capabilityError.value = ''; capabilityUnavailable.value = '' }
function invalidateDetails(): void { ++detailSequence; clearCapabilityState(); showDetail.value = false; selected.value = null; detailError.value = '' }
watch([siteId, groupId, () => auth.user?.username, () => auth.user?.role], invalidateDetails, { flush: 'sync' })
watch(showDetail, visible => { if (!visible) { ++detailSequence; clearCapabilityState() } }, { flush: 'sync' })
onDeactivated(invalidateDetails)
onBeforeUnmount(invalidateDetails)

async function loadCapabilities(sequence = detailSequence): Promise<void> {
  const detail = selected.value
  if (!detail || !showDetail.value) return
  const profileId = detail.terminalProfileId
  clearCapabilityState()
  if (detail.item.vendor.trim().toUpperCase() !== 'H3C' || detail.item.deviceType.toUpperCase() === 'FIT_AP') {
    capabilityUnavailable.value = '当前能力证据仅适用于独立管理的 H3C 设备；FIT-AP 请查看无线业务中的 AC 观测。'; return
  }
  if (!profileId || detail.terminalProfile?.id !== profileId || (detail.item.terminalProfileId && detail.item.terminalProfileId !== profileId)) {
    capabilityUnavailable.value = '未配置当前授权范围内的连接，能力状态 UNKNOWN。'; return
  }
  const current = () => sequence === detailSequence && showDetail.value && selected.value === detail
  capabilityLoading.value = true
  try {
    const next = await getH3cOperationalCapabilities(profileId)
    if (!current()) return
    if (next.profileId !== profileId) throw new Error('设备能力关联不一致')
    operationalCapabilities.value = next
  } catch { if (current()) capabilityError.value = '设备运行能力读取失败，请重试；缺少证据的能力保持 UNKNOWN。' }
  finally { if (current()) capabilityLoading.value = false }
}

function stopConnectionPolling(): void { if (connectionPoll !== undefined) clearTimeout(connectionPoll); connectionPoll = undefined }
function invalidateConnectionTests(message = ''): void {
  ++connectionContext; stopConnectionPolling()
  connectionPlan.value = null; connectionReport.value = null; connectionTargets.value = []; connectionPlanBusy.value = false; connectionPlanMessage.value = message
}
function currentConnectionContext(context: number): boolean { return connectionActive && canWrite.value && context === connectionContext }
async function checkConnectionModules(context: number): Promise<boolean> {
  try {
    const modules = await getModuleStates()
    if (!currentConnectionContext(context)) return false
    if (['devices', 'h3c'].every(key => modules.some(module => module.key === key && module.enabled))) return true
    invalidateConnectionTests('批量连接测试所需模块已停用或不可用')
  } catch { if (currentConnectionContext(context)) invalidateConnectionTests('批量连接测试模块状态暂不可用，请刷新后重试') }
  return false
}
function connectionPlanStateLabel(state: string): string { return state === 'PARTIAL_FAILED' ? '部分失败' : taskStateLabel(state) }
function connectionPlanError(cause: unknown): void {
  let message = '批量连接测试状态读取失败，请刷新重试'
  if (cause instanceof ApiRequestError) {
    if (cause.status === 400) message = '请选择 1 至 64 台不同设备'
    else if ([401, 403, 404].includes(cause.status)) { invalidateConnectionTests('设备或任务不存在，或当前权限已发生变化'); return }
    else if (cause.status === 409) message = '当前仅支持已配置连接的独立 H3C 设备，请检查所选设备'
    else if (cause.status === 503) { invalidateConnectionTests('批量连接测试服务或模块暂不可用'); return }
  }
  connectionPlanMessage.value = message
}
function scheduleConnectionRefresh(context: number): void {
  if (!currentConnectionContext(context) || !connectionPlanRunning.value) return
  if (++connectionPollCount > 200) { connectionPlanMessage.value = '任务仍在运行，请稍后刷新结果或到任务中心查看'; return }
  connectionPoll = setTimeout(() => { connectionPoll = undefined; void refreshConnectionTests(context) }, 1500)
}
async function refreshConnectionTests(context = connectionContext): Promise<void> {
  const id = connectionPlan.value?.id
  if (!id || connectionPlanBusy.value || !currentConnectionContext(context)) return
  stopConnectionPolling(); connectionPlanBusy.value = true; connectionPlanMessage.value = ''
  let loaded = false
  try {
    if (!await checkConnectionModules(context)) return
    const next = await getH3cTaskPlanReport(id)
    if (!currentConnectionContext(context) || !await checkConnectionModules(context)) return
    if (next.plan.id !== id) throw new Error('任务关联不一致')
    connectionPlan.value = next.plan; connectionReport.value = next; loaded = true
  } catch (cause) { if (currentConnectionContext(context)) connectionPlanError(cause) }
  finally {
    if (currentConnectionContext(context)) { connectionPlanBusy.value = false; if (loaded) scheduleConnectionRefresh(context) }
  }
}
async function startConnectionTests(): Promise<void> {
  if (!connectionActive || !canWrite.value || connectionPlanBusy.value || connectionPlanRunning.value || !selectedIds.value.length || selectedIds.value.length > 64) return
  invalidateConnectionTests(); const context = connectionContext
  const ids = [...selectedIds.value]
  connectionTargets.value = ids.map(id => selectedTargets.get(id) || { id, name: id })
  connectionPollCount = 0; connectionPlanBusy.value = true
  try {
    if (!await checkConnectionModules(context)) return
    const next = await createDeviceConnectionTests(ids)
    if (!currentConnectionContext(context) || !await checkConnectionModules(context)) return
    connectionPlan.value = next
  } catch (cause) { if (currentConnectionContext(context)) connectionPlanError(cause) }
  finally { if (currentConnectionContext(context)) connectionPlanBusy.value = false }
  if (currentConnectionContext(context) && connectionPlan.value) await refreshConnectionTests(context)
}
async function cancelConnectionTests(): Promise<void> {
  const id = connectionPlan.value?.id; const context = connectionContext
  if (!id || !connectionPlanRunning.value || connectionPlanBusy.value || !currentConnectionContext(context)) return
  stopConnectionPolling(); connectionPlanBusy.value = true; connectionPlanMessage.value = ''
  try {
    if (!await checkConnectionModules(context)) return
    const next = await cancelH3cTaskPlan(id)
    if (!currentConnectionContext(context) || !await checkConnectionModules(context)) return
    connectionPlan.value = next; connectionReport.value = null
  } catch (cause) { if (currentConnectionContext(context)) connectionPlanError(cause) }
  finally { if (currentConnectionContext(context)) connectionPlanBusy.value = false }
  if (currentConnectionContext(context) && connectionPlan.value) await refreshConnectionTests(context)
}
watch(siteId, () => { clearSelection(); invalidateConnectionTests() }, { flush: 'sync' })
watch(canWrite, () => { clearSelection(); invalidateConnectionTests() }, { flush: 'sync' })
onActivated(() => { connectionActive = true; if (detailsReady) openRouteDetail() })
onDeactivated(() => { connectionActive = false; clearSelection(); invalidateConnectionTests() })
onBeforeUnmount(() => { connectionActive = false; invalidateConnectionTests() })

async function refreshSites(): Promise<void> {
  try {
    const next = await getDeviceSites()
    sites.value = next
    if (siteId.value && !next.some(site => site.id === siteId.value)) siteId.value = next[0]?.id || ''
    if (!siteId.value && next.length) siteId.value = next[0].id
    form.siteId = siteId.value || next[0]?.id || ''
    groupForm.siteId = siteId.value || next[0]?.id || ''
  } catch { sites.value = [] }
}

async function refresh(): Promise<void> {
  const sequence = ++refreshSequence
  loading.value = true; unsupported.value = false
  try {
    const next = await getDeviceInventory({ page: page.value, pageSize: pageSize.value, search: search.value, status: status.value, vendor: vendor.value, deviceType: deviceType.value, siteId: siteId.value, groupId: groupId.value })
    if (sequence === refreshSequence) result.value = next
  } catch (cause) {
    if (sequence !== refreshSequence) return
    unsupported.value = cause instanceof ApiRequestError && [404, 503].includes(cause.status)
    if (!unsupported.value) ElMessage.error('设备库存 API 读取失败')
  } finally { if (sequence === refreshSequence) loading.value = false }
}
async function refreshGroups(): Promise<void> {
  const sequence = ++groupSequence
  try { const next = await getDeviceGroups(siteId.value); if (sequence === groupSequence) groups.value = next } catch { if (sequence === groupSequence) ElMessage.error('设备分组读取失败') }
}
function applyFilters(): void { page.value = 1; void refresh() }
function selectSite(nextSiteId: string): void {
  siteId.value = nextSiteId
  groupId.value = ''
  form.siteId = nextSiteId || sites.value[0]?.id || ''
  groupForm.siteId = nextSiteId || sites.value[0]?.id || ''
  void Promise.all([refresh(), refreshGroups()])
}
function selectScope(nextGroupId = ''): void { groupId.value = nextGroupId; applyFilters() }
function clearFilters(): void { search.value = ''; status.value = ''; vendor.value = ''; deviceType.value = ''; groupId.value = ''; applyFilters() }
function rememberConnectionTarget(row: DeviceInventoryItem): void { selectedTargets.set(row.id, { id: row.id, name: row.name, profileId: row.terminalProfileId }) }
function onSelectionChange(id: string, event: Event): void { const checked = (event.target as HTMLInputElement).checked; const row = rows.value.find(item => item.id === id); if (checked && row) rememberConnectionTarget(row); else selectedTargets.delete(id); selectedIds.value = checked ? Array.from(new Set([...selectedIds.value, id])) : selectedIds.value.filter(item => item !== id) }
function togglePageSelection(): void { const remove = allRowsSelected.value; rows.value.forEach(row => { if (remove) selectedTargets.delete(row.id); else rememberConnectionTarget(row) }); selectedIds.value = remove ? selectedIds.value.filter(id => !rows.value.some(row => row.id === id)) : Array.from(new Set([...selectedIds.value, ...rows.value.map(row => row.id)])) }
function clearSelection(): void { selectedIds.value = []; selectedTargets.clear() }
function newIdempotencyKey(): string { return globalThis.crypto?.randomUUID?.() || `batch-${Date.now()}-${Math.random().toString(16).slice(2)}` }
function openBatch(kind: DeviceBatchKind): void { if (!canWrite.value || !selectedIds.value.length) return; batchKind.value = kind; batchSite.value = siteId.value || 'local'; batchGroup.value = ''; Object.assign(batchMetadata, { name: '', vendor: '', model: '', deviceType: '', description: '' }); operationId.value = newIdempotencyKey(); showBatchDialog.value = true }
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
function resetForm(): void { Object.assign(form, { id: '', name: '', managementIp: '', protocol: 'SSH', port: 22, username: '', password: '', vendor: '', model: '', deviceType: 'NETWORK_DEVICE', siteId: siteId.value || sites.value[0]?.id || 'local', groupId: '', description: '', enabled: true, terminalProfileId: '' }) }
function openCreate(): void { resetForm(); showEditor.value = true }
function openOnboard(): void {
  Object.assign(onboardForm, { host: '', protocol: 'SSH', port: 22, username: '', password: '', allowTelnetFallback: false, vendor: 'H3C', platform: '', name: '', description: '' })
  showOnboard.value = true
}
function updateOnboardProtocol(): void { onboardForm.port = onboardForm.protocol === 'TELNET' ? 23 : 22 }
async function onboardDevice(): Promise<void> {
  if (!canWrite.value) return
  if (!onboardForm.host.trim() || (!onboardForm.username.trim() && onboardForm.protocol !== 'TELNET') || !onboardForm.password) {
    ElMessage.warning(onboardForm.protocol === 'TELNET' ? '请填写 IP 地址和密码' : '请填写 IP 地址、用户名和密码'); return
  }
  saving.value = true
  try {
    const selectedSiteId = siteId.value || 'local'
    const profile = await onboardH3cProfile({
      host: onboardForm.host.trim(), siteId: selectedSiteId, protocol: onboardForm.protocol, port: onboardForm.port,
      username: onboardForm.username.trim(), password: onboardForm.password, allowTelnetFallback: onboardForm.allowTelnetFallback,
      inventory: { name: onboardForm.name.trim() || onboardForm.host.trim(), managementIp: onboardForm.host.trim(), vendor: 'H3C', model: '', deviceType: 'NETWORK_DEVICE', siteId: selectedSiteId, groupId: null, description: onboardForm.description.trim(), enabled: true, terminalProfileId: null },
    })
    onboardForm.password = ''
    showOnboard.value = false
    await refresh()
    const test = await testH3cConnection(profile.id)
    if (test.status === 'CONNECTED') ElMessage.success('设备已保存，连接测试成功')
    else ElMessage.warning(`设备已添加：${test.message}`)
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备添加失败') }
  finally { saving.value = false }
}
async function openEdit(row: DeviceInventoryItem): Promise<void> {
  try {
    selected.value = await getDeviceInventoryDetail(row.id)
    const item = selected.value.item
    const profile = selected.value.terminalProfile
    Object.assign(form, {
      id: item.id, name: item.name, managementIp: item.managementIp || item.managementAddress,
      protocol: profile?.terminalProtocol || item.protocol || 'SSH', port: profile?.port || item.port || 22,
      username: profile?.username || item.username || '', password: '', vendor: item.vendor === '—' ? '' : item.vendor,
      model: item.model === '—' ? '' : item.model, deviceType: item.deviceType, siteId: item.siteId || 'local',
      groupId: item.groupId || '', description: selected.value.description, enabled: item.enabled,
      terminalProfileId: item.terminalProfileId || '',
    })
    showEditor.value = true
  } catch { ElMessage.error('设备详情读取失败') }
}
function openRouteDetail(): void {
  if (!detailsReady || !connectionActive) return
  invalidateDetails()
  if (route.path !== '/devices') return
  const id = route.query.deviceId
  if (typeof id === 'string' && id.trim()) void openDetails({ id })
  else if (id != null) detailError.value = '设备链接无效，请从设备列表重新选择。'
}
watch(() => [route.path, route.query.deviceId], openRouteDetail, { flush: 'sync' })
async function openDetails(row: Pick<DeviceInventoryItem, 'id'>): Promise<void> {
  if (!connectionActive) return
  invalidateDetails(); const sequence = detailSequence
  backupModuleState.value = 'unknown'
  try {
    const [detail, modules] = await Promise.all([getDeviceInventoryDetail(row.id), getModuleStates().catch(() => null)])
    if (sequence !== detailSequence) return
    if (detail.item.id !== row.id) throw new Error('设备关联不一致')
    selected.value = detail
    const module = modules?.find(item => item.key === 'config-backups')
    backupModuleState.value = module ? module.enabled ? 'enabled' : 'disabled' : 'unknown'
    showDetail.value = true
    await loadCapabilities(sequence)
  } catch { if (sequence === detailSequence) { detailError.value = '设备不存在、无权查看或详情读取失败，请从设备列表重新选择。'; ElMessage.error('设备详情读取失败') } }
}
function openConfigBackups(): void {
  if (!selected.value || backupUnavailableReason.value) return
  showDetail.value = false
  void router.push({ path: '/config-backups', query: { deviceId: selected.value.item.id } })
}
async function testConnection(): Promise<void> {
  const profileId = selected.value?.terminalProfileId || selected.value?.item.terminalProfileId
  if (!profileId) { ElMessage.warning('该设备尚未配置连接'); return }
  connectionTesting.value = true; connectionResult.value = null
  try {
    connectionResult.value = await testH3cConnection(profileId)
    if (connectionResult.value.status === 'CONNECTED') ElMessage.success('连接成功，设备信息已识别')
    else ElMessage.warning(connectionResult.value.message || '连接未成功')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '测试连接失败') }
  finally { connectionTesting.value = false }
}
function payload(): Record<string, unknown> { return { id: form.id || undefined, name: form.name, managementIp: form.managementIp, vendor: form.vendor, model: form.model, deviceType: form.deviceType, siteId: form.siteId, groupId: form.groupId || null, description: form.description, enabled: form.enabled, terminalProfileId: form.terminalProfileId || null } }
async function save(): Promise<void> {
  if (!canWrite.value) return
  if (!form.id && form.deviceType === 'FIT_AP') { ElMessage.warning('FIT-AP 必须由 AC 管理，不能作为独立设备创建'); return }
  if (!form.managementIp.trim() || (!form.username.trim() && form.protocol !== 'TELNET') || (!form.id && !form.password)) {
    ElMessage.warning(form.protocol === 'TELNET' ? '请填写 IP 地址和密码' : '请填写 IP 地址、用户名和密码'); return
  }
  saving.value = true
  try {
    if (form.id && form.terminalProfileId) {
      await createH3cProfile({
        id: form.terminalProfileId, host: form.managementIp.trim(), port: form.port, username: form.username.trim(),
        credentialReference: '', hostKeySha256: '', enabled: form.enabled, hostKeyUpdatePolicy: 'AUTOMATIC',
        allowLegacyAlgorithms: false, terminalEncoding: 'AUTO', terminalProtocol: form.protocol,
        allowTelnetFallback: false, password: form.password || undefined,
      })
      form.password = ''
    }
    if (form.id) await updateDevice(form.id, payload())
    else await createDevice(payload())
    ElMessage.success(form.id ? '设备已更新' : '设备已创建'); showEditor.value = false; await refresh()
  }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备保存失败') }
  finally { saving.value = false }
}
async function toggle(row: DeviceInventoryItem): Promise<void> {
  if (!canWrite.value) return
  try { const detail = await getDeviceInventoryDetail(row.id); const item = detail.item; await updateDevice(row.id, { id: row.id, name: item.name, managementIp: item.managementIp || item.managementAddress, vendor: item.vendor, model: item.model, deviceType: item.deviceType, siteId: item.siteId || 'local', groupId: item.groupId || null, description: detail.description, enabled: !item.enabled, terminalProfileId: item.terminalProfileId || null }); ElMessage.success(item.enabled ? '设备已禁用' : '设备已启用'); await refresh() }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备状态更新失败') }
}
function openInner(row: DeviceInventoryItem): void { if (row.terminalProfileId) router.push({ path: '/terminal', query: { deviceId: row.terminalProfileId } }); else ElMessage.warning('该设备尚未配置连接') }
async function quickTest(row: DeviceInventoryItem): Promise<void> {
  if (!row.terminalProfileId) { ElMessage.warning('该设备尚未配置连接'); return }
  connectionTesting.value = true
  try {
    const output = await testH3cConnection(row.terminalProfileId)
    if (output.status === 'CONNECTED') ElMessage.success(`连接成功：${output.vendor || '设备'} ${output.version || ''}`)
    else ElMessage.warning(output.message || '连接未成功')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '测试连接失败') }
  finally { connectionTesting.value = false }
}
async function createGroup(): Promise<void> { try { await createDeviceGroup(groupForm); ElMessage.success('分组已创建'); showGroup.value = false; groupForm.name = ''; groupForm.description = ''; await refreshGroups() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '分组创建失败') } }
function onFile(event: Event): void { const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return; const reader = new FileReader(); reader.onload = () => { importCsv.value = String(reader.result || ''); void previewImport() }; reader.readAsText(file, 'UTF-8') }
async function previewImport(): Promise<void> { try { importPreview.value = await previewDeviceImport(importCsv.value); showImport.value = true } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : 'CSV 预览失败') } }
async function commitImport(): Promise<void> { if (!importPreview.value || importPreview.value.conflictCount || importPreview.value.errorCount) return; try { const output = await commitDeviceImport(importPreview.value.previewId); ElMessage.success(`导入完成：新增 ${output.created}，更新 ${output.updated}`); showImport.value = false; importPreview.value = null; await refresh() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '导入提交失败') } }
function importFile(): void { document.getElementById('device-import-file')?.click() }
onMounted(async () => { const saved = sessionStorage.getItem('netconsole.deviceBatchOperationId'); if (saved) { operationId.value = saved; try { batchOperation.value = await getDeviceBatchOperation(saved); showBatchPreview.value = true } catch { sessionStorage.removeItem('netconsole.deviceBatchOperationId') } } await refreshSites(); await Promise.all([refresh(), refreshGroups()]); detailsReady = true; openRouteDetail() })
</script>

<template>
  <NcLayout title="设备管理" description="输入 IP、协议、端口、账号和密码即可连接；安全存储由服务端自动处理">
    <template #actions>
      <el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button size="small" @click="showGroup = true">分组管理</el-button><el-button size="small" @click="importFile"><el-icon><Upload /></el-icon>CSV 导入</el-button><el-button v-if="canWrite" type="primary" size="small" @click="openOnboard">新增设备</el-button><input id="device-import-file" type="file" accept=".csv,text/csv" hidden @change="onFile" />
    </template>
    <p v-if="detailError" role="alert">{{ detailError }}</p>
    <div v-if="unsupported" class="card"><div class="empty-state"><div><el-icon><Monitor /></el-icon><strong>设备库存 API 尚未启用</strong><small>当前服务未提供设备查询能力；页面不会使用前端模拟数据。</small></div></div></div>
    <template v-else>
      <div class="inventory-summary"><span class="summary-chip"><b>{{ summary.totalRecords }}</b> 条设备</span><span class="summary-chip summary-chip--success"><b>{{ summary.enabledDevices }}</b> 已启用</span><span class="summary-chip"><b>{{ summary.disabledDevices }}</b> 已禁用</span><span class="summary-chip summary-chip--info"><b>{{ summary.onlineDevices }}</b> 最近在线</span><span class="summary-chip"><b>{{ summary.unknownStatus }}</b> 状态未知</span></div>
      <div v-if="summary.invalidRecords" class="data-quality-note"><el-icon><Warning /></el-icon>服务端报告 {{ summary.invalidRecords }} 条结构异常记录，异常不被隐藏。</div>
      <div class="device-management-workspace" data-testid="device-management-workspace">
        <aside class="device-scope-panel card" aria-label="设备作用域导航">
          <div class="device-scope-panel__head"><div><span class="section-kicker">SCOPE</span><h2>设备作用域</h2></div><span class="scope-lock">授权</span></div>
          <label class="form-field scope-site-select"><span>授权局点</span><select v-model="siteId" @change="selectSite(siteId)"><option value="">全部已授权局点</option><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.name }} · {{ site.id }}</option></select></label>
          <button class="scope-item scope-item--site" :class="{ 'is-active': !groupId }" type="button" @click="selectScope('')"><span><strong>{{ siteScopeLabel }}</strong><small class="mono">siteId: {{ siteId || 'ALL_AUTHORIZED_SITES' }}</small></span><b>{{ summary.totalRecords }}</b></button>
          <div class="scope-divider"><span>分组</span><span>{{ groups.length }}</span></div>
          <button v-for="group in groups" :key="group.id" class="scope-item" :class="{ 'is-active': groupId === group.id }" type="button" @click="selectScope(group.id)"><span><strong>{{ group.name }}</strong><small>{{ group.description || '无描述' }}</small></span><b>{{ group.deviceCount }}</b></button>
          <div v-if="!groups.length" class="scope-empty">当前局点暂无服务端分组</div>
          <div class="scope-note"><el-icon><CircleCheck /></el-icon><span>站点列表以服务端授权范围为准；本页不会猜测或扩展局点。</span></div>
        </aside>
        <div class="device-list-region">
          <section class="card">
            <div class="filter-bar"><label class="global-search filter-input"><el-icon><Search /></el-icon><input v-model="search" placeholder="搜索名称、设备 ID、管理 IP、厂商" @keydown.enter="applyFilters" /></label><select v-model="vendor" aria-label="设备厂商" @change="applyFilters"><option value="">全部厂商</option><option v-for="option in vendorOptions" :key="option" :value="option">{{ option }}</option></select><select v-model="deviceType" aria-label="设备类型" @change="applyFilters"><option value="">全部类型</option><option v-for="option in deviceTypeOptions" :key="option" :value="option">{{ option }}</option></select><select v-model="status" aria-label="设备状态" @change="applyFilters"><option value="">全部状态</option><option value="ENABLED">已启用</option><option value="DISABLED">已禁用</option><option value="ONLINE">最近在线</option><option value="OFFLINE">最近离线</option><option value="UNKNOWN">状态未知</option></select><button class="icon-button" type="button" title="清除筛选" @click="clearFilters"><el-icon><Filter /></el-icon></button><span class="filter-bar__grow" /><span class="muted">服务端分页 · 最新更新时间排序</span></div>
        <div v-if="canWrite" class="batch-toolbar"><label class="batch-select-all"><input type="checkbox" :checked="allRowsSelected" aria-label="批量当前页全选" @change="togglePageSelection" />当前页全选</label><span class="muted">已选择 {{ selectedIds.length }} 台（跨页选择会保留明确设备 ID）</span><el-button size="small" :loading="connectionPlanBusy" :disabled="!selectedIds.length || selectedIds.length > 64 || connectionPlanBusy || connectionPlanRunning" @click="startConnectionTests">批量测试连接</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_ENABLE')">批量启用</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_DISABLE')">批量禁用</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_SET_GROUP')">设置分组</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_SET_SITE')">设置局点</el-button><el-button size="small" :disabled="!selectedIds.length" @click="openBatch('BATCH_UPDATE_METADATA')">更新元数据</el-button><el-button v-if="selectedIds.length" text size="small" @click="clearSelection">清除选择</el-button></div>
        <div v-loading="loading" class="table-wrap device-table-wrap" data-testid="device-inventory-table"><table class="data-table"><thead><tr><th><input type="checkbox" :checked="allRowsSelected" aria-label="批量当前页全选" @change="togglePageSelection" /></th><th>状态</th><th>设备名称 / ID</th><th>管理 IP</th><th>厂商 / 型号</th><th>类型</th><th>局点 / 分组</th><th>连接</th><th>更新时间</th><th>操作</th></tr></thead><tbody><tr v-for="device in rows" :key="device.id" :class="{ 'is-invalid': !device.isValid }" @dblclick="openDetails(device)"><td><input type="checkbox" :checked="selectedIds.includes(device.id)" :aria-label="`选择设备 ${device.name}`" @change="onSelectionChange(device.id, $event)" /></td><td><span :class="['state-pill', statusClass(device.connectionStatus)]"><i class="status-dot" />{{ statusLabel(device.connectionStatus) }}</span></td><td><span class="device-name">{{ device.name }}</span><span class="device-subline mono">{{ device.deviceId || device.id }}<span v-if="!device.isValid" class="device-quality">· {{ device.qualityIssues.join(' / ') }}</span></span></td><td class="device-address">{{ device.managementIp || device.managementAddress }}</td><td>{{ device.vendor }}<span class="device-subline">{{ device.model }}</span></td><td>{{ device.deviceType }}</td><td>{{ device.site }}<span class="device-subline">{{ device.group }}</span></td><td><span :class="['state-pill', device.terminalProfileId ? 'good' : 'neutral']">{{ device.terminalProfileId ? '已配置' : '未配置' }}</span></td><td>{{ formatTime(device.updatedAt || device.lastSeenMs) }}</td><td><div class="table-actions"><el-button text size="small" @click="openDetails(device)">详情</el-button><el-button v-if="canWrite" text size="small" @click="openEdit(device)">编辑</el-button><el-button v-if="canWrite" text size="small" @click="toggle(device)">{{ device.enabled ? '禁用' : '启用' }}</el-button><el-button text size="small" :disabled="!device.terminalProfileId" @click="quickTest(device)">测试连接</el-button><el-button text size="small" :disabled="!device.terminalProfileId" @click="openInner(device)">终端</el-button></div></td></tr><tr v-if="!rows.length"><td colspan="10"><div class="empty-state"><div><el-icon><Document /></el-icon><strong>{{ hasFilters ? '没有符合条件的设备' : '暂无设备' }}</strong><small>新增设备或导入 CSV 后，数据会从 Catalog 真实读取。</small></div></div></td></tr></tbody></table></div>
        <div class="pagination-bar"><span>第 {{ page }} 页 · 每页 {{ pageSize }} 条 · 共 {{ result?.total || 0 }} 条</span><div class="toolbar"><select v-model.number="pageSize" @change="page = 1; refresh()"><option :value="20">20 / 页</option><option :value="50">50 / 页</option><option :value="100">100 / 页</option></select><el-button size="small" :disabled="page <= 1" @click="page--; refresh()">上一页</el-button><el-button size="small" :disabled="!result?.hasMore" @click="page++; refresh()">下一页</el-button></div></div>
          </section>
        </div>
      </div>
    </template>
    <section v-if="canWrite && (connectionPlan || connectionPlanBusy || connectionPlanMessage)" class="card" data-testid="device-connection-batch" aria-label="批量连接测试结果" aria-live="polite">
      <div class="card__body">
        <div class="overview-footer"><strong>批量连接测试</strong><span v-if="connectionPlan">{{ connectionPlanStateLabel(connectionPlan.state) }} · 已完成 {{ connectionPlanCompleted }} / {{ connectionPlan.items.length }}</span><span v-else-if="connectionPlanBusy">正在提交所选设备</span></div>
        <p class="muted">仅测试明确选中的设备。本次结果反映连接及只读命令执行情况。</p>
        <p v-if="connectionPlanMessage" role="status">{{ connectionPlanMessage }}</p>
        <template v-if="connectionPlan">
          <p class="mono">任务计划：{{ connectionPlan.id }}</p>
          <progress :value="connectionPlanCompleted" :max="connectionPlan.items.length || 1" aria-label="批量连接测试进度" />
          <div class="toolbar"><el-button size="small" :disabled="connectionPlanBusy" @click="refreshConnectionTests()">刷新结果</el-button><el-button v-if="connectionPlanRunning" size="small" :disabled="connectionPlanBusy" @click="cancelConnectionTests">取消测试</el-button></div>
          <div class="table-wrap"><table class="data-table"><thead><tr><th>设备 / 库存 ID</th><th>状态</th><th>子任务</th><th>命令结果</th></tr></thead><tbody>
            <tr v-for="entry in connectionPlanRows" :key="entry.item.ordinal">
              <td>{{ entry.target?.name || entry.item.target }}<small class="device-subline mono">{{ entry.target?.id || '库存关联待核对' }}</small><small class="device-subline mono">设备连接：{{ entry.item.target }}</small></td>
              <td>{{ connectionPlanStateLabel(entry.item.state) }}<small v-if="entry.item.error || entry.report?.run?.failureReason" class="device-subline">{{ taskFailureLabel(entry.item.error || entry.report?.run?.failureReason) }}</small></td>
              <td class="mono">{{ entry.item.runId || '待调度' }}</td>
              <td><div v-for="command in entry.report?.commands || []" :key="command.index"><span class="mono">{{ command.command }}</span> · {{ connectionPlanStateLabel(command.state) }}<small v-if="command.error" class="device-subline">{{ taskFailureLabel(command.error) }}</small></div><span v-if="!entry.report?.commands.length" class="muted">暂无命令结果</span></td>
            </tr>
          </tbody></table></div>
        </template>
        <el-button size="small" @click="router.push('/tasks')">打开任务中心</el-button>
      </div>
    </section>
    <div class="section-label">数据与安全口径</div><div class="card card--flat"><div class="card__body overview-footer"><span><el-icon><CircleCheck /></el-icon>连接时只需填写设备地址、协议、端口、用户名和密码</span><span><el-icon><CircleCheck /></el-icon>密码不会显示在设备列表、任务、日志或响应中</span><span><el-icon><Warning /></el-icon>CSV 不接受密码、凭据或私钥列，导入先预览再原子提交</span></div></div>
    <el-dialog v-model="showOnboard" title="连接设备" width="620px"><div class="form-grid"><label class="form-field"><span>IP 地址</span><input v-model="onboardForm.host" placeholder="192.168.56.2" required /></label><label class="form-field"><span>协议</span><select v-model="onboardForm.protocol" @change="updateOnboardProtocol"><option value="SSH">SSH</option><option value="TELNET">Telnet</option><option value="AUTO">自动协商</option></select></label><p v-if="onboardForm.protocol === 'TELNET' || (onboardForm.protocol === 'AUTO' && onboardForm.allowTelnetFallback)" class="form-hint">Telnet 使用明文传输。</p><label class="form-field"><span>端口</span><input v-model.number="onboardForm.port" type="number" min="1" max="65535" /></label><label class="form-field"><span>用户名</span><input v-model="onboardForm.username" autocomplete="username" :required="onboardForm.protocol !== 'TELNET'" /><small v-if="onboardForm.protocol === 'TELNET'" class="form-hint">密码模式设备可留空。</small></label><label class="form-field"><span>密码</span><input v-model="onboardForm.password" type="password" autocomplete="current-password" required /><small class="form-hint">只在本次连接请求中输入；服务端负责必要的安全存储，页面不显示密码。</small></label><label v-if="onboardForm.protocol === 'AUTO'" class="form-field" style="align-items:center"><span>SSH 失败时尝试 Telnet</span><input v-model="onboardForm.allowTelnetFallback" type="checkbox" /></label><label class="form-field"><span>设备名称（可选）</span><input v-model="onboardForm.name" placeholder="留空使用 IP 地址" /></label><label class="form-field"><span>描述（可选）</span><input v-model="onboardForm.description" /></label></div><p class="muted">保存后自动测试连接；设备类型和必要连接参数由服务端识别。当前局点：{{ siteScopeLabel }}</p><template #footer><el-button @click="showOnboard = false">取消</el-button><el-button type="primary" :loading="saving" @click="onboardDevice">保存并测试连接</el-button></template></el-dialog>
    <el-dialog v-model="showEditor" :title="form.id ? '编辑设备' : '新增设备'" width="660px"><div class="form-grid"><label class="form-field"><span>设备 ID</span><input v-model="form.id" :disabled="!!form.id" placeholder="留空自动生成" /></label><label class="form-field"><span>设备名称</span><input v-model="form.name" required /></label><label class="form-field"><span>IP 地址</span><input v-model="form.managementIp" placeholder="192.0.2.10" /></label><label class="form-field"><span>协议</span><select v-model="form.protocol"><option value="SSH">SSH</option><option value="TELNET">Telnet</option><option value="AUTO">自动协商</option></select></label><label class="form-field"><span>端口</span><input v-model.number="form.port" type="number" min="1" max="65535" /></label><label class="form-field"><span>用户名</span><input v-model="form.username" autocomplete="username" :required="form.protocol !== 'TELNET'" /><small v-if="form.protocol === 'TELNET'" class="form-hint">密码模式设备可留空。</small></label><label class="form-field"><span>密码</span><input v-model="form.password" type="password" autocomplete="current-password" /><small class="form-hint">留空保持现有密码；需要更换时直接输入新密码。</small></label><label class="form-field"><span>厂商</span><input v-model="form.vendor" list="device-vendor-options" /><datalist id="device-vendor-options"><option v-for="option in vendorOptions" :key="option" :value="option" /></datalist></label><label class="form-field"><span>型号</span><input v-model="form.model" /></label><label class="form-field"><span>设备类型</span><select v-model="form.deviceType"><option v-for="option in editableDeviceTypeOptions" :key="option" :value="option">{{ option }}</option><option v-if="form.id && form.deviceType === 'FIT_AP'" value="FIT_AP">FIT_AP · 由 AC 管理</option></select><small class="form-hint">FIT-AP 必须由 AC 管理，本页不提供独立创建入口。</small></label><label class="form-field"><span>局点</span><input v-model="form.siteId" disabled /><small class="form-hint">当前授权作用域：{{ siteScopeLabel }}</small></label><label class="form-field"><span>分组</span><select v-model="form.groupId"><option value="">未分组</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }}</option></select></label><label class="form-field"><span>描述</span><input v-model="form.description" /></label><label class="form-field" style="align-items:center"><span>启用</span><input v-model="form.enabled" type="checkbox" /></label></div><template #footer><el-button @click="showEditor = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存</el-button></template></el-dialog>
    <el-dialog v-model="showDetail" title="设备详情 / 审计" width="800px"><template v-if="selected"><div class="detail-hero"><div><span class="section-kicker">DEVICE DETAIL</span><h2>{{ selected.item.name }}</h2><p class="mono">{{ selected.item.id }}</p></div><div class="detail-hero__badges"><span :class="['state-pill', statusClass(selected.item.connectionStatus)]">{{ statusLabel(selected.item.connectionStatus) }}</span><span class="scope-flag">{{ selected.item.site }} / {{ selected.item.group }}</span></div></div><section class="detail-section"><div class="detail-section__head"><h3>基础信息</h3><span class="muted">稳定身份与当前作用域</span></div><div class="detail-grid"><div><span>管理 IP</span><b class="mono">{{ selected.item.managementIp || selected.item.managementAddress }}</b></div><div><span>厂商 / 型号</span><b>{{ selected.item.vendor }} / {{ selected.item.model }}</b></div><div><span>设备类型</span><b>{{ selected.item.deviceType }}</b></div><div><span>局点 / 分组</span><b>{{ selected.item.site }} / {{ selected.item.group }}</b></div><div><span>连接状态</span><b>{{ selected.item.credentialConfigured ? '已配置' : '未配置' }}</b></div><div><span>连接</span><b>{{ selected.terminalProfileId ? '已配置' : '未配置' }}</b></div><div><span>创建时间</span><b>{{ formatTime(selected.createdMs) }}</b></div><div><span>更新时间</span><b>{{ formatTime(selected.updatedMs) }}</b></div></div><p class="muted">{{ selected.description || '无描述' }}</p></section><section class="detail-section"><div class="detail-section__head"><h3>可用能力</h3><span class="muted">只展示已有真实 API 的状态</span></div><div class="detail-capability-grid"><div><span>终端连接</span><b>{{ selected.terminalProfileId ? '已配置' : '未配置' }}</b><small>{{ selected.terminalProfileId ? '可从设备列表进入终端控制台' : '请先填写 IP、协议、端口、用户名和密码' }}</small></div><div><span>配置备份</span><el-button size="small" :disabled="!!backupUnavailableReason" @click="openConfigBackups">查看配置备份</el-button><small>{{ backupUnavailableReason || '查看当前设备的备份历史、版本差异与下载' }}</small></div><div><span>接口 / LLDP</span><b>能力证据</b><small>见下方设备运行能力，按当前连接设置展示</small></div><div><span>任务 / 事实</span><b>独立模块</b><small>通过任务中心或厂商事实页查看</small></div></div></section><section class="detail-section" aria-label="设备运行能力" data-testid="device-operational-capabilities">
      <div class="detail-section__head"><h3>设备运行能力</h3><el-button size="small" :disabled="capabilityLoading" @click="loadCapabilities()">刷新能力证据</el-button></div>
      <p v-if="capabilityLoading" role="status">正在读取设备能力证据…</p>
      <p v-else-if="capabilityError" role="alert">{{ capabilityError }}</p>
      <p v-else-if="capabilityUnavailable" class="detail-empty">{{ capabilityUnavailable }}</p>
      <template v-else-if="operationalCapabilities">
        <div class="detail-grid"><div><span>已采集型号</span><b>{{ operationalCapabilities.model || 'UNKNOWN' }}</b></div><div><span>已采集版本 / Release</span><b>{{ operationalCapabilities.version || 'UNKNOWN' }} / {{ operationalCapabilities.release || 'UNKNOWN' }}</b></div><div><span>控制器报告角色</span><b>{{ operationalCapabilities.controllerRole || 'UNKNOWN' }} → {{ operationalCapabilities.controllerRoleNormalized || 'UNKNOWN' }}</b></div></div>
        <p class="muted">证据截至 {{ formatTime(operationalCapabilities.asOf) }}。VERIFIED 表示近期原始采集已验证；STALE 表示证据过期；UNKNOWN 表示缺少验证。设备报告角色不代表 HA 切换成功。</p>
        <div v-if="!operationalCapabilities.capabilities.length" class="detail-empty">暂无设备能力证据，状态 UNKNOWN。</div>
        <div v-else class="table-wrap"><table class="data-table"><thead><tr><th>能力</th><th>状态 / 新鲜度</th><th>来源 / 捕获时间</th><th>证据 / 原因</th></tr></thead><tbody><tr v-for="capability in operationalCapabilities.capabilities" :key="capability.key"><td>{{ capabilityLabels[capability.key] || capability.key }}</td><td>{{ capability.state }} / {{ capability.freshness }}</td><td>{{ capability.source }}<br />{{ capability.capturedMs ? formatTime(capability.capturedMs) : '无捕获时间' }}</td><td>{{ capability.evidenceIds.join(' / ') || '无证据' }}<br />{{ capability.reason }}</td></tr></tbody></table></div>
      </template>
      <p v-else class="detail-empty">暂无设备能力证据，状态 UNKNOWN。</p>
    </section><DeviceLldpNeighbors v-if="showDetail && selected.item.vendor.trim().toUpperCase() === 'H3C' && selected.item.deviceType.toUpperCase() !== 'FIT_AP'" :profile-id="selected.terminalProfile?.id === selected.terminalProfileId && (!selected.item.terminalProfileId || selected.item.terminalProfileId === selected.terminalProfileId) ? selected.terminalProfileId || '' : ''" :device-id="selected.item.id" :site-id="selected.item.siteId || ''" @open-topology="showDetail = false; router.push('/topology')" /><div v-if="selected.item.qualityIssues.length" class="data-quality-note"><el-icon><Warning /></el-icon>{{ selected.item.qualityIssues.join(' / ') }}</div><section class="detail-section"><div class="detail-section__head"><h3>最近连接会话</h3><span class="muted">保留历史会话</span></div><div v-if="!selected.recentSessions.length" class="detail-empty">暂无持久化连接历史</div><div v-for="session in selected.recentSessions" :key="session.id" class="detail-event"><span>{{ formatTime(session.startedMs) }} · {{ session.protocol }}</span><span>{{ session.state }} · {{ session.recordingStatus }}</span></div></section><section class="detail-section"><div class="detail-section__head"><h3>审计记录</h3><span class="muted">服务端写入结果</span></div><div v-if="!selected.auditEvents.length" class="detail-empty">暂无审计记录</div><div v-for="audit in selected.auditEvents" :key="audit.id" class="detail-event"><span>{{ formatTime(audit.atMs) }} · {{ audit.action }}</span><span>{{ audit.outcome }}</span></div></section></template></el-dialog>
    <el-dialog v-model="showGroup" title="设备分组管理" width="520px"><div class="form-grid"><label class="form-field"><span>局点</span><input v-model="groupForm.siteId" disabled /><small class="form-hint">只能在当前授权局点创建分组。</small></label><label class="form-field"><span>分组名称</span><input v-model="groupForm.name" /></label><label class="form-field"><span>描述</span><input v-model="groupForm.description" /></label></div><div class="overview-footer" style="margin-top:16px"><span v-for="group in groups" :key="group.id">{{ group.name }} · {{ group.deviceCount }} 台</span></div><template #footer><el-button @click="showGroup = false">关闭</el-button><el-button v-if="canWrite" type="primary" @click="createGroup">新增分组</el-button></template></el-dialog>
    <el-dialog v-model="showImport" title="CSV 导入预览" width="820px"><p class="muted">预览不会写入数据库；只有无冲突、明确确认后才会执行单事务提交。支持 create / update，任何错误都会阻止整批写入。</p><div v-if="importPreview" class="inventory-summary"><span class="summary-chip"><b>{{ importPreview.totalRows }}</b> 行</span><span class="summary-chip summary-chip--success"><b>{{ importPreview.createCount }}</b> 新增</span><span class="summary-chip summary-chip--info"><b>{{ importPreview.updateCount }}</b> 更新</span><span class="summary-chip summary-chip--danger"><b>{{ importPreview.conflictCount + importPreview.errorCount }}</b> 冲突/错误</span></div><div class="table-wrap"><table class="data-table"><thead><tr><th>行</th><th>动作</th><th>名称</th><th>管理 IP</th><th>说明</th></tr></thead><tbody><tr v-for="row in importPreview?.rows || []" :key="row.line"><td>{{ row.line }}</td><td>{{ row.action }}</td><td>{{ row.name }}</td><td>{{ row.managementIp }}</td><td>{{ row.detail }}</td></tr></tbody></table></div><template #footer><el-button @click="showImport = false">取消</el-button><el-button type="primary" :disabled="!importPreview || !!(importPreview.conflictCount || importPreview.errorCount)" @click="commitImport">确认原子导入</el-button></template></el-dialog>
    <el-dialog v-model="showBatchDialog" :title="batchDialogKindLabel" width="620px"><p class="muted">本次操作只提交明确选择的 {{ selectedIds.length }} 台设备。服务端会记录版本并在确认时再次校验。</p><div class="form-grid"><label v-if="batchKind === 'BATCH_SET_SITE'" class="form-field"><span>目标局点</span><input v-model="batchSite" required /></label><label v-if="batchKind === 'BATCH_SET_GROUP'" class="form-field"><span>目标分组</span><select v-model="batchGroup"><option value="">请选择分组</option><option v-for="group in groups" :key="group.id" :value="group.id">{{ group.name }} · {{ group.siteId }}</option></select></label><template v-if="batchKind === 'BATCH_UPDATE_METADATA'"><label class="form-field"><span>名称（留空保持）</span><input v-model="batchMetadata.name" /></label><label class="form-field"><span>厂商（留空保持）</span><input v-model="batchMetadata.vendor" /></label><label class="form-field"><span>型号（留空保持）</span><input v-model="batchMetadata.model" /></label><label class="form-field"><span>设备类型（留空保持）</span><input v-model="batchMetadata.deviceType" /></label><label class="form-field"><span>描述（留空保持）</span><input v-model="batchMetadata.description" /></label></template></div><template #footer><el-button @click="showBatchDialog = false">取消</el-button><el-button type="primary" :loading="batchLoading" :disabled="batchKind === 'BATCH_SET_GROUP' && !batchGroup" @click="createBatchPreview">生成预览</el-button></template></el-dialog>
    <el-dialog v-model="showBatchPreview" title="批量操作预览" width="900px"><template v-if="batchOperation"><div class="inventory-summary"><span class="summary-chip"><b>{{ batchKindLabel }}</b></span><span class="summary-chip"><b>{{ batchOperation.targetCount }}</b> 台目标</span><span class="summary-chip" :class="batchOperation.state === 'CONFLICT' ? 'summary-chip--danger' : 'summary-chip--success'"><b>{{ operationStateLabel }}</b></span><span class="summary-chip">已完成 {{ batchOperation.completedCount }} · 冲突 {{ batchOperation.conflictCount }} · 失败 {{ batchOperation.failedCount }}</span></div><p class="muted">操作 ID：<span class="mono">{{ batchOperation.id }}</span> · 幂等键与操作状态已保存，刷新页面可恢复。</p><div class="table-wrap"><table class="data-table"><thead><tr><th>设备 ID</th><th>预期版本</th><th>预览结果</th><th>说明</th></tr></thead><tbody><tr v-for="item in batchOperation.items" :key="item.deviceId"><td class="mono">{{ item.deviceId }}</td><td>{{ item.expectedRevision }}</td><td>{{ item.outcome }}</td><td>{{ item.detail }}</td></tr></tbody></table></div><p v-if="batchOperation.state === 'CONFLICT'" class="data-quality-note"><el-icon><Warning /></el-icon>预览发现冲突，服务端不会允许确认；请刷新设备数据后重新选择并生成新预览。</p></template><template #footer><el-button :loading="batchLoading" @click="reloadBatch">重新加载状态</el-button><el-button v-if="batchOperation?.canCancel" :loading="batchLoading" @click="cancelBatch">取消操作</el-button><el-button v-if="batchOperation?.canConfirm" type="primary" :loading="batchLoading" @click="confirmBatch">{{ batchOperation?.canRetry ? '安全重试' : '确认批量提交' }}</el-button><el-button @click="showBatchPreview = false">关闭</el-button></template></el-dialog>
  </NcLayout>
</template>
