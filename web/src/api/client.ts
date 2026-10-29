export interface WebUser {
  username: string
  role: 'ADMIN' | 'SUPERADMIN'
}

export interface HealthSnapshot {
  status: string
  serviceState: string
  healthState: string
  healthError?: string | null
  collectionState: string
  collectionError?: string | null
  h3cCollectionEnabled: boolean
  syslogEnabled: boolean
  rawWriteAvailable: boolean
  rawSegments?: { finalized: number; incomplete: number; missing: number }
  udpReceived: number
  rawFlushedRecords: number
  h3cMetrics?: { activeSshSessions: number; collectionFailures: number }
  unresolvedIntegrityIssues?: number
  resources?: {
    processCpuPercent?: number | null
    workingSetBytes: number
    threadCount: number
    handleCount?: number | null
    dataVolumeFreeBytes?: number | null
    sampledAtMs?: number
  }
  instanceId?: string
}

export interface DeviceInventorySummary {
  totalRecords: number
  validDevices: number
  invalidRecords: number
  enabledDevices: number
  disabledDevices: number
  onlineDevices: number
  offlineDevices: number
  unknownStatus: number
}

export interface DeviceInventoryItem {
  id: string
  name: string
  managementAddress: string
  port: number
  username: string
  vendor: string
  model: string
  deviceType: string
  protocol: string
  site: string
  group: string
  enabled: boolean
  connectionStatus: string
  lastSeenMs?: number | null
  credentialConfigured: boolean
  hostKeyUpdatePolicy: string
  terminalEncoding: string
  lastDisconnectReason?: string | null
  isValid: boolean
  qualityIssues: string[]
  deviceId?: string
  managementIp?: string
  siteId?: string
  groupId?: string
  description?: string
  createdAt?: number
  updatedAt?: number
  terminalProfileId?: string | null
}

export interface DeviceInventoryPage {
  page: number
  pageSize: number
  total: number
  hasMore: boolean
  summary: DeviceInventorySummary
  items: DeviceInventoryItem[]
}

export interface DeviceGroup {
  id: string
  siteId: string
  name: string
  description: string
  createdMs: number
  updatedMs: number
  deviceCount: number
}

export interface DeviceInventoryDetail {
  item: DeviceInventoryItem
  description: string
  createdMs: number
  updatedMs: number
  terminalProfileId?: string | null
  terminalProfile?: TerminalDevice | null
  recentSessions: TerminalSession[]
  auditEvents: Array<{ id: string; atMs: number; action: string; outcome: string; detailJson: string }>
}
export interface ZteProfile {
  id: string
  host: string
  port: number
  username: string
  enabled: boolean
  createdMs: number
  updatedMs: number
  terminalEncoding: string
  siteId: string
}
export interface ZteCollectionRun {
  id: string
  profileId: string
  state: string
  startedMs: number
  endedMs?: number | null
  failureReason?: string | null
  commandStatuses: Record<string, string>
}
export interface ZteFact {
  profileId: string
  kind: string
  status: string
  capturedMs: number
  payloadJson: string
  runId?: string | null
  isCurrent: boolean
}

export interface DeviceImportPreview {
  previewId: string
  expiresAtMs: number
  totalRows: number
  createCount: number
  updateCount: number
  conflictCount: number
  errorCount: number
  rows: Array<{ line: number; action: string; deviceId: string; name: string; managementIp: string; detail: string; canCommit: boolean }>
}

export type DeviceBatchKind = 'BATCH_ENABLE' | 'BATCH_DISABLE' | 'BATCH_SET_GROUP' | 'BATCH_SET_SITE' | 'BATCH_UPDATE_METADATA'
export interface DeviceBatchRequest {
  kind: DeviceBatchKind
  idempotencyKey: string
  deviceIds: string[]
  siteId?: string
  groupId?: string
  name?: string
  vendor?: string
  model?: string
  deviceType?: string
  description?: string
}
export interface DeviceBatchOperationItem {
  deviceId: string
  expectedRevision: number
  beforeJson: string
  requestedJson: string
  outcome: string
  detail: string
}
export interface DeviceBatchOperation {
  id: string
  actor: string
  kind: DeviceBatchKind
  state: string
  targetCount: number
  completedCount: number
  conflictCount: number
  failedCount: number
  createdMs: number
  updatedMs: number
  startedMs?: number | null
  completedMs?: number | null
  requestJson: string
  progressJson: string
  canConfirm: boolean
  canCancel: boolean
  canRetry: boolean
  items: DeviceBatchOperationItem[]
}

export interface P2B3Page<T> {
  limit: number
  total: number
  hasMore: boolean
  nextCursor?: string | null
  items: T[]
}
export interface P2B3Scope {
  scopeKind: string
  scopeReference: string
  resolution: string
  sourceKind: string
  sourceReference: string
  revision: number
}
export interface P2B3Address {
  id: string
  deviceId: string
  networkDomainId?: string | null
  addressFamily: string
  normalizedAddress: string
  prefixLength: number
  gatewayAddress?: string | null
  vrfName: string
  addressRole: string
  interfaceReference: string
  sourceKind: string
  sourceReference: string
  revision: number
  observedAtMs?: number | null
  validFromMs?: number | null
  validToMs?: number | null
  domainResolutionState: string
  conflictState: string
  lifecycle: string
  effectiveSourceKind?: string | null
  effectiveSourceReference?: string | null
  effectiveState: string
}
export interface P2B3Membership {
  id: string
  deviceId: string
  networkDomainId: string
  domainName: string
  domainKind: string
  membershipRole: string
  sourceKind: string
  sourceReference: string
  revision: number
  lifecycle: string
  scopeResolution: string
  scopes: P2B3Scope[]
  conflictState: string
}
export interface P2B3Domain {
  id: string
  name: string
  domainKind: string
  parentDomainId?: string | null
  vrfName: string
  sourceKind: string
  sourceReference: string
  revision: number
  lifecycle: string
  scopeResolution: string
  scopes: P2B3Scope[]
  authorizationState: string
}
export interface P2B3VlanPlan {
  id: string
  networkDomainId: string
  vlanId: number
  name: string
  purpose: string
  taggingMode: string
  nativeVlanId?: number | null
  pvid?: number | null
  qinQOuterId?: number | null
  qinQInnerId?: number | null
  scopeResolution: string
  sourceKind: string
  sourceReference: string
  revision: number
  lifecycle: string
  validFromMs?: number | null
  validToMs?: number | null
  conflictState: string
  effectiveState: string
  effectiveSourceKind?: string | null
  effectiveSourceReference?: string | null
}
export interface P2B3DiffFact {
  id: string
  sourceKind: string
  sourceReference: string
  revision: number
  networkDomainId?: string | null
  interfaceReference: string
  value: string
  conflictState: string
  domainResolutionState: string
  lifecycle: string
  validFromMs?: number | null
  validToMs?: number | null
  factCount: number
  incomplete: boolean
}
export interface P2B3DiffItem {
  key: string
  status: string
  effectiveState: string
  effectiveSourceKind?: string | null
  effectiveSourceReference?: string | null
  planned: P2B3DiffFact[]
  observed: P2B3DiffFact[]
}
export interface P2B3NetworkDiff {
  deviceId: string
  asOfMs?: number | null
  truncated: boolean
  addressItems: P2B3DiffItem[]
  vlanItems: P2B3DiffItem[]
}
export interface P2B4WriteResult {
  operationId: string
  state: string
  entityId?: string | null
  code?: string | null
  detail?: string | null
}
export interface P2B4ImportStatus {
  importId: string
  operationId: string
  state: string
  rowCount: number
  validCount: number
  errors: string[]
  createdMs: number
  updatedMs: number
}
export interface P2B4Operation {
  id: string
  actor: string
  kind: string
  state: string
  request?: Record<string, unknown>
  progress: Record<string, unknown>
  createdMs: number
  updatedMs: number
  startedMs?: number | null
  completedMs?: number | null
}
export interface P2B4History {
  targetId: string
  revision: number
  snapshotJson: string
  archivedMs: number
}
export interface P2B4Review {
  id: string
  actor: string
  targetType: string
  targetId: string
  sourceKind: string
  sourceReference: string
  targetRevision: number
  decision: string
  reason: string
  conflictState: string
  createdMs: number
}

export interface TerminalDevice {
  id: string
  host: string
  port: number
  username: string
  enabled: boolean
  supportedProtocols: string[]
  hostKeyUpdatePolicy: string
  allowLegacyAlgorithms: boolean
  terminalEncoding: string
  terminalProtocol: string
  allowTelnetFallback: boolean
  lastSessionState?: string | null
  lastSessionStartedMs?: number | null
  lastDisconnectReason?: string | null
}

export interface TerminalSession {
  id: string
  targetId: string
  targetName: string
  protocol: string
  state: string
  startedMs: number
  endedMs?: number | null
  recordingStatus: string
  recordingComplete: boolean
  commandCount: number
  outputBytes: number
  recordingBytes: number
  disconnectReason?: string | null
}

export interface TaskRun {
  id: string
  sourceId?: string | null
  taskType: string
  state: string
  queuedMs?: number | null
  startedMs?: number | null
  endedMs?: number | null
  failureReason?: string | null
  definitionId?: string | null
  rawOutputPath?: string | null
  captureStatus?: string | null
  parseStatus?: string | null
}

export interface HostTool {
  id: string
  displayName: string
  product: string
  executablePath?: string | null
  status: string
  configured: boolean
  isWindowsTool: boolean
  detail?: string | null
}

export interface FileRoot {
  id: string
  label: string
  path: string
  exists: boolean
}

export interface FileEntry {
  name: string
  relativePath: string
  kind: 'FILE' | 'DIRECTORY'
  size: number
  modifiedMs: number
  canDownload: boolean
  canArchive: boolean
}

export interface FileListResult {
  rootId: string
  relativePath: string
  items: FileEntry[]
  page: number
  pageSize: number
  total: number
  hasMore: boolean
}

export interface ArchiveJob {
  id: string
  state: string
  selectedCount: number
  processedCount: number
  processedBytes: number
  totalBytes?: number | null
  current?: string | null
  errorCode?: string | null
  error?: string | null
  createdMs: number
  completedMs?: number | null
  downloadName?: string | null
  canDownload: boolean
  canCancel: boolean
}

export class ApiRequestError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'ApiRequestError'
  }
}

let csrfToken = ''

export function setCsrfToken(token: string): void {
  csrfToken = token
}

export function clearCsrfToken(): void {
  csrfToken = ''
}

async function request<T>(path: string, init: RequestInit = {}, allowErrorBody = false): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (init.method && !['GET', 'HEAD'].includes(init.method.toUpperCase()) && csrfToken) {
    headers.set('X-NC-CSRF', csrfToken)
  }
  const response = await fetch(path, { ...init, headers, credentials: 'include' })
  if (!response.ok) {
    const body = await response.text()
    if (allowErrorBody) throw new ApiRequestError(response.status, body || response.statusText)
    let message = body || response.statusText
    try {
      const parsed = JSON.parse(body)
      message = parsed.detail ? `${parsed.error || response.statusText}: ${parsed.detail}` : parsed.error || message
    } catch { /* plain text */ }
    throw new ApiRequestError(response.status, message)
  }
  if (response.status === 204) return undefined as T
  const type = response.headers.get('content-type') || ''
  return type.includes('application/json') ? response.json() as Promise<T> : response.text() as Promise<T>
}

export async function getCsrf(): Promise<string> {
  const result = await request<{ csrfToken: string }>('/api/auth/csrf')
  setCsrfToken(result.csrfToken)
  return result.csrfToken
}

export async function login(username: string, password: string): Promise<{ user: WebUser; csrfToken: string }> {
  const result = await request<{ user: WebUser; csrfToken: string }>('/api/auth/login', {
    method: 'POST', body: JSON.stringify({ username, password }),
  })
  setCsrfToken(result.csrfToken)
  return result
}

export function getCurrentUser(): Promise<WebUser> { return request('/api/auth/me') }
export async function logout(): Promise<void> {
  try { await request('/api/auth/logout', { method: 'POST' }) }
  finally { clearCsrfToken() }
}
export function changePassword(payload: { currentPassword?: string; newPassword: string; username?: string }): Promise<void> {
  return request('/api/auth/password', { method: 'POST', body: JSON.stringify(payload) })
}

export function getHealth(): Promise<HealthSnapshot> { return request('/api/system/health') }
export function getDeviceInventory(params: { page?: number; pageSize?: number; search?: string; status?: string; siteId?: string; groupId?: string } = {}): Promise<DeviceInventoryPage> {
  const query = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => { if (value !== undefined && value !== '') query.set(key, String(value)) })
  return request('/api/system/devices?' + query.toString())
}
export function getDeviceInventoryDetail(id: string): Promise<DeviceInventoryDetail> { return request('/api/system/devices/' + encodeURIComponent(id)) }
export function getZteProfiles(): Promise<ZteProfile[]> { return request('/api/system/zte/profiles') }
export function createZteProfile(payload: Record<string, unknown>): Promise<ZteProfile> {
  return request('/api/system/zte/profiles', { method: 'POST', body: JSON.stringify(payload) })
}
export function startZteCollection(id: string): Promise<{ runId: string }> {
  return request(`/api/system/zte/profiles/${encodeURIComponent(id)}/collect`, { method: 'POST', body: JSON.stringify({}) })
}
export function getZteRun(id: string): Promise<ZteCollectionRun> { return request(`/api/system/zte/runs/${encodeURIComponent(id)}`) }
export function getZteFacts(id: string, params: { kind?: string; history?: boolean; limit?: number } = {}): Promise<ZteFact[]> {
  return request(queryPath(`/api/system/zte/profiles/${encodeURIComponent(id)}/facts`, params))
}
function queryPath(path: string, params: Record<string, unknown>): string {
  const query = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => { if (value !== undefined && value !== null && value !== '') query.set(key, String(value)) })
  const encoded = query.toString()
  return encoded ? `${path}?${encoded}` : path
}
export function getDeviceAddresses(deviceId: string, params: { limit?: number; cursor?: string; domainId?: string; sourceKind?: string; addressFamily?: string; lifecycle?: string } = {}): Promise<P2B3Page<P2B3Address>> {
  return request(queryPath(`/api/system/devices/${encodeURIComponent(deviceId)}/addresses`, params))
}
export function getDeviceMemberships(deviceId: string, params: { limit?: number; cursor?: string; siteId?: string; domainId?: string; lifecycle?: string } = {}): Promise<P2B3Page<P2B3Membership>> {
  return request(queryPath(`/api/system/devices/${encodeURIComponent(deviceId)}/network-memberships`, params))
}
export function getNetworkDomains(params: { limit?: number; cursor?: string; siteId?: string; stationId?: string; lineId?: string; sectionId?: string; domainKind?: string; scopeResolution?: string; lifecycle?: string; search?: string } = {}): Promise<P2B3Page<P2B3Domain>> {
  return request(queryPath('/api/system/network-domains', params))
}
export function getVlanPlans(params: { limit?: number; cursor?: string; domainId?: string; siteId?: string; sourceKind?: string; scopeResolution?: string; lifecycle?: string; vlanId?: number; asOf?: number } = {}): Promise<P2B3Page<P2B3VlanPlan>> {
  return request(queryPath('/api/system/vlan-plans', params))
}
export function getNetworkDiff(deviceId: string, params: { domainId?: string; resolutionState?: string; asOf?: number } = {}): Promise<P2B3NetworkDiff> {
  return request(queryPath(`/api/system/devices/${encodeURIComponent(deviceId)}/network-diff`, params))
}
export function createNetworkAddress(deviceId: string, payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request(`/api/system/devices/${encodeURIComponent(deviceId)}/addresses`, { method: 'POST', body: JSON.stringify(payload) })
}
export function updateNetworkAddress(deviceId: string, addressId: string, payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request(`/api/system/devices/${encodeURIComponent(deviceId)}/addresses/${encodeURIComponent(addressId)}`, { method: 'PUT', body: JSON.stringify(payload) })
}
export function createNetworkMembership(deviceId: string, payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request(`/api/system/devices/${encodeURIComponent(deviceId)}/network-memberships`, { method: 'POST', body: JSON.stringify(payload) })
}
export function createVlanPlan(payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request('/api/system/vlan-plans', { method: 'POST', body: JSON.stringify(payload) })
}
export function updateVlanPlan(planId: string, payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request(`/api/system/vlan-plans/${encodeURIComponent(planId)}`, { method: 'PUT', body: JSON.stringify(payload) })
}
export function createNetworkReview(payload: Record<string, unknown>): Promise<P2B4WriteResult> {
  return request('/api/system/network-reviews', { method: 'POST', body: JSON.stringify(payload) })
}
export function createNetworkImport(payload: { idempotencyKey: string; csv: string; expiresMs?: number }): Promise<P2B4ImportStatus> {
  return request('/api/system/import-runs', { method: 'POST', body: JSON.stringify(payload) })
}
export function getNetworkImport(importId: string): Promise<P2B4ImportStatus> { return request(`/api/system/import-runs/${encodeURIComponent(importId)}`) }
export function dryRunNetworkImport(importId: string): Promise<P2B4ImportStatus> { return request(`/api/system/import-runs/${encodeURIComponent(importId)}/dry-run`, { method: 'POST' }) }
export function confirmNetworkImport(importId: string): Promise<P2B4ImportStatus> { return request(`/api/system/import-runs/${encodeURIComponent(importId)}/confirm`, { method: 'POST' }) }
export function cancelNetworkImport(importId: string): Promise<P2B4ImportStatus> { return request(`/api/system/import-runs/${encodeURIComponent(importId)}/cancel`, { method: 'POST' }) }
export function getP2B4Operation(operationId: string): Promise<P2B4Operation> { return request(`/api/system/p2b4-operations/${encodeURIComponent(operationId)}`) }
export function getP2B4Operations(limit = 50): Promise<P2B4Operation[]> { return request(queryPath('/api/system/p2b4-operations', { limit })) }
export function getNetworkReviews(params: { targetType?: string; targetId?: string; limit?: number } = {}): Promise<P2B4Review[]> { return request(queryPath('/api/system/network-reviews', params)) }
export function getAddressHistory(deviceId: string, addressId: string): Promise<P2B4History[]> { return request(`/api/system/devices/${encodeURIComponent(deviceId)}/addresses/${encodeURIComponent(addressId)}/history`) }
export function getVlanHistory(planId: string): Promise<P2B4History[]> { return request(`/api/system/vlan-plans/${encodeURIComponent(planId)}/history`) }
export function getDeviceGroups(siteId?: string): Promise<DeviceGroup[]> {
  const query = siteId ? '?' + new URLSearchParams({ siteId }).toString() : ''
  return request('/api/system/device-groups' + query)
}
export function createDeviceGroup(payload: { siteId: string; name: string; description?: string }): Promise<DeviceGroup> {
  return request('/api/system/device-groups', { method: 'POST', body: JSON.stringify(payload) })
}
export function createDevice(payload: Record<string, unknown>): Promise<DeviceInventoryDetail> {
  return request('/api/system/devices', { method: 'POST', body: JSON.stringify(payload) })
}
export function updateDevice(id: string, payload: Record<string, unknown>): Promise<DeviceInventoryDetail> {
  return request('/api/system/devices/' + encodeURIComponent(id), { method: 'PUT', body: JSON.stringify(payload) })
}
export function previewDeviceImport(csv: string): Promise<DeviceImportPreview> {
  return request('/api/system/devices/import/preview', { method: 'POST', body: JSON.stringify({ csv }) })
}
export function commitDeviceImport(previewId: string): Promise<{ previewId: string; created: number; updated: number; committedAtMs: number }> {
  return request('/api/system/devices/import/commit', { method: 'POST', body: JSON.stringify({ previewId, confirm: true }) })
}
export function createDeviceBatch(payload: DeviceBatchRequest): Promise<DeviceBatchOperation> {
  return request('/api/system/devices/batch', { method: 'POST', body: JSON.stringify(payload) })
}
export function getDeviceBatchOperation(id: string): Promise<DeviceBatchOperation> {
  return request('/api/system/device-operations/' + encodeURIComponent(id))
}
export function confirmDeviceBatch(id: string): Promise<DeviceBatchOperation> {
  return request('/api/system/devices/batch/' + encodeURIComponent(id) + '/confirm', { method: 'POST' })
}
export function cancelDeviceBatch(id: string): Promise<DeviceBatchOperation> {
  return request('/api/system/devices/batch/' + encodeURIComponent(id) + '/cancel', { method: 'POST' })
}
export function getTerminalDevices(): Promise<TerminalDevice[]> { return request('/api/system/terminal/devices') }
export function getH3cProfiles(): Promise<TerminalDevice[]> { return request('/api/system/h3c/profiles') }
export function getSerialPorts(): Promise<string[]> { return request('/api/system/terminal/serial-ports') }
export function getTasks(): Promise<TaskRun[]> { return request('/api/system/tasks') }
export function getHostTools(): Promise<HostTool[]> { return request('/api/system/host-tools') }
export function updateHostTool(id: string, executablePath: string | null): Promise<HostTool> {
  return request('/api/system/host-tools/' + encodeURIComponent(id), { method: 'PUT', body: JSON.stringify({ executablePath }) })
}
export function launchHostTool(toolId: string, deviceId: string): Promise<{ status: string; detail?: string | null; toolId: string; deviceId: string }> {
  return request('/api/system/host-tools/' + encodeURIComponent(toolId) + '/launch', { method: 'POST', body: JSON.stringify({ deviceId }) })
}
export function openHostDirectory(rootId: string, relativePath: string): Promise<{ status: string; detail?: string | null }> {
  return request('/api/system/host-tools/open-directory', { method: 'POST', body: JSON.stringify({ rootId, relativePath }) })
}
export function getFileRoots(): Promise<FileRoot[]> { return request('/api/system/files/roots') }
export function listFiles(params: { rootId: string; path?: string; search?: string; sort?: string; direction?: string; page?: number; pageSize?: number }): Promise<FileListResult> {
  const query = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => { if (value !== undefined) query.set(key, String(value)) })
  return request('/api/system/files/list?' + query.toString())
}
export async function downloadFile(rootId: string, path: string): Promise<Blob> {
  const response = await fetch('/api/system/files/download?' + new URLSearchParams({ rootId, path }), { credentials: 'include' })
  if (!response.ok) throw new ApiRequestError(response.status, await response.text())
  return response.blob()
}
export function createArchiveJob(selections: Array<{ rootId: string; paths: string[] }>): Promise<ArchiveJob> {
  return request('/api/system/files/archive-jobs', { method: 'POST', body: JSON.stringify({ selections }) })
}
export function getArchiveJob(id: string): Promise<ArchiveJob> {
  return request('/api/system/files/archive-jobs/' + encodeURIComponent(id))
}
export function cancelArchiveJob(id: string): Promise<void> {
  return request('/api/system/files/archive-jobs/' + encodeURIComponent(id) + '/cancel', { method: 'POST' })
}
export async function downloadArchive(id: string): Promise<Blob> {
  const response = await fetch('/api/system/files/archive-jobs/' + encodeURIComponent(id) + '/download', { credentials: 'include' })
  if (!response.ok) throw new ApiRequestError(response.status, await response.text())
  return response.blob()
}

export function createH3cProfile(payload: Record<string, unknown>): Promise<TerminalDevice> {
  return request('/api/system/h3c/profiles', { method: 'POST', body: JSON.stringify(payload) })
}

export function createTerminalSession(payload: Record<string, unknown>): Promise<{ sessionId: string }> {
  return request('/api/system/terminal/sessions', { method: 'POST', body: JSON.stringify(payload) })
}
export function issueTerminalTicket(sessionId: string): Promise<{ ticket: string; expiresAtMs: number }> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/ticket`, { method: 'POST' })
}
export function closeTerminalSession(sessionId: string): Promise<void> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/close`, { method: 'POST' })
}
export function getTerminalSessions(): Promise<{ items: TerminalSession[]; total: number }> {
  return request('/api/system/terminal/sessions?page=1&pageSize=100')
}
export function getTerminalEvents(sessionId: string): Promise<Array<{ atMs: number; eventType: string; detailJson: string }>> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/events?limit=500`)
}
export function getRecordingIntegrity(sessionId: string): Promise<{ status: string; detail: string; hashMatches: boolean }> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/recording/integrity`)
}
export function getReplay(sessionId: string, offset = 0): Promise<{ entries: Array<{ atMs: number; direction: string; dataB64: string }>; hasMore: boolean }> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/recording/replay?offset=${offset}&limit=2000`)
}
export async function downloadExport(sessionId: string, format: string): Promise<Blob> {
  const response = await fetch(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/export?format=${encodeURIComponent(format)}`, { credentials: 'include' })
  if (!response.ok) throw new ApiRequestError(response.status, await response.text())
  return response.blob()
}

