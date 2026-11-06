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

export interface ModuleState {
  key: string
  label: string
  enabled: boolean
  isCore: boolean
  updatedMs: number
  updatedBy: string
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

export interface DeviceSite {
  id: string
  name: string
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
  credentialConfigured: boolean
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

export interface H3cConnectionTestResult {
  deviceId: string
  host: string
  port: number
  protocol: string
  status: string
  message: string
  vendor?: string | null
  version?: string | null
  hostKeyDecision?: string | null
  elapsedMs: number
}

export interface TerminalSession {
  id: string
  targetId: string
  targetName: string
  protocol: string
  mode?: 'RESTRICTED_READONLY' | 'LINUX_INTERACTIVE'
  operatorIdentity?: string
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

export interface TerminalSessionQuery {
  page?: number
  pageSize?: number
  keyword?: string
  protocol?: string
}

export interface TerminalSessionPage {
  page: number
  pageSize: number
  total: number
  items: TerminalSession[]
}

export interface LinuxServer {
  id: string
  name: string
  host: string
  port: number
  username: string
  groupName: string
  siteId: string
  enabled: boolean
  credentialConfigured: boolean
  hostKeySha256: string
  lastConnectedMs?: number | null
  lastUsedMs?: number | null
  lastResult?: string | null
}
export interface LinuxServerWrite {
  name: string
  host: string
  port: number
  username: string
  password?: string
  clearPassword?: boolean
  groupName: string
  siteId: string
  enabled: boolean
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
export interface TaskCommand {
  index: number
  command: string
  state: string
  error?: string | null
  rawOutputPath?: string | null
  captureStatus?: string | null
  parseStatus?: string | null
  capturedMs?: number | null
}
export interface TaskDiagnosticCommand {
  index: number
  command: string
  availability: 'READY' | 'NO_RAW' | 'CONFIG_EXCLUDED' | 'UNSUPPORTED_COMMAND' | 'TASK_ACTIVE' | 'RAW_INVALID'
  canDownload: boolean
  captureStatus: string | null
  parseStatus: string | null
  capturedMs: number | null
  byteCount: number | null
  outputSha256: string | null
  segmentSha256: string | null
  downloadByteCount?: number | null
  downloadSha256?: string | null
  redacted?: boolean
}
export interface TaskDiagnosticManifest {
  version: number
  runId: string
  taskState: string
  status: 'COMPLETE' | 'PARTIAL' | 'UNAVAILABLE'
  canDownload: boolean
  commands: TaskDiagnosticCommand[]
}
export interface H3cTaskPlanItem {
  ordinal: number
  target: string
  definitionId: string
  runId?: string | null
  state: string
  error?: string | null
}
export interface H3cTaskPlan {
  id: string
  taskType: string
  state: string
  actorUserId: string
  commandPlan: string[]
  claimsRevision: string
  createdMs: number
  updatedMs: number
  items: H3cTaskPlanItem[]
}
export interface H3cTaskPlanReport {
  plan: H3cTaskPlan
  items: Array<{ item: H3cTaskPlanItem; run?: TaskRun | null; commands: TaskCommand[] }>
}

export type NetworkDiagnosticKind = 'PING' | 'TCP' | 'DNS' | 'LLDP' | 'TRACEROUTE'
export interface NetworkTraceRoute {
  destinationAddress: string
  destinationReached: boolean
  maxHops: number
  hops: Array<{ ttl: number; address: string | null; status: string; latencyMs: number | null }>
}
export interface NetworkDiagnosticItem {
  id: string
  deviceId: string
  deviceName: string
  siteId: string
  kind: NetworkDiagnosticKind
  state: string
  outcome?: string | null
  detail?: string | null
  startedMs?: number | null
  endedMs?: number | null
  latencyMs?: number | null
  evidenceCount: number
  evidenceSources: string[]
  traceRoute?: NetworkTraceRoute | null
}
export interface NetworkDiagnosticRun {
  id: string
  actor: string
  state: string
  siteId?: string | null
  timeoutMs: number
  totalDevices: number
  totalChecks: number
  completedChecks: number
  succeededChecks: number
  failedChecks: number
  noEvidenceChecks: number
  cancelledChecks: number
  queuedChecks: number
  runningChecks: number
  progressPercent: number
  createdMs: number
  startedMs?: number | null
  endedMs?: number | null
  failureReason?: string | null
  items: NetworkDiagnosticItem[]
}

export interface NetworkDiagnosticRunSummary {
  id: string
  state: string
  totalDevices: number
  totalChecks: number
  completedChecks: number
  progressPercent: number
  createdMs: number
  startedMs?: number | null
  endedMs?: number | null
  kinds: NetworkDiagnosticKind[]
}

export interface H3cFact {
  id: string
  profileId: string
  kind: string
  identityKey: string
  status: string
  source: string
  capturedMs: number
  isCurrent: boolean
  payloadJson: string
  collectedRawVerified?: boolean
  dataState?: string
  freshness?: string
}

export interface H3cLldpRelation {
  profileId: string
  localDeviceId: string
  localInterface: string
  neighborDevice: string
  neighborPort?: string | null
  neighborManagementAddress?: string | null
  neighborChassisId?: string | null
  siteId: string
  source: string
  capturedMs: number
  factRevision: string
  relationKey: string
  changeKind: string
}

export interface H3cLldpClaims {
  profileId: string
  scopeState: string
  isGlobal: boolean
  claimsRevision?: string | null
  allowedSiteReferences: string[]
  factCount: number
  relationCount: number
  relations: H3cLldpRelation[]
}

export type ConfigBackupKind = 'RUNNING' | 'SAVED' | 'STARTUP_INFO' | 'STARTUP_BODY'

export interface ConfigBackupSchedule {
  id: string
  ownerUserId: string
  profileIds: string[]
  configurationKind: ConfigBackupKind
  intervalMinutes: number
  enabled: boolean
  createdMs: number
  nextRunMs?: number | null
  lastRunMs?: number | null
  lastCompletedMs?: number | null
  lastState?: string | null
  lastError?: string | null
  lastBatchId?: string | null
}

export interface ConfigBackupSnapshot {
  id: string
  profileId: string
  deviceId: string
  siteId: string
  version: number
  state: string
  createdMs: number
  capturedMs?: number | null
  sha256?: string | null
  byteCount: number
  taskRunId?: string | null
  error?: string | null
  canDownload: boolean
  downloadName?: string | null
  configurationKind?: ConfigBackupKind
  command?: string | null
}

export interface ConfigBackupBatch {
  id: string
  actorUserId: string
  state: string
  createdMs: number
  completedMs?: number | null
  total: number
  completed: number
  succeeded: number
  failed: number
  items: Array<{ profileId: string; state: string; snapshotId?: string | null; error?: string | null }>
  configurationKind?: ConfigBackupKind
}

export interface ConfigBackupDiff {
  baseId: string
  targetId: string
  profileId: string
  identical: boolean
  addedLines: number
  removedLines: number
  changedLines: number
  added: string[]
  removed: string[]
  configurationKind?: ConfigBackupKind
}

export interface WirelessScope {
  status: string
  claimsRevision: string
}

export interface WirelessAcMember {
  deviceId: string
  displayName: string
  siteId: string
  managementIp: string
  vendor: string
  model: string
  version?: string | null
  role: string
  runtimeState: string
  connectionStatus: string
  enabled: boolean
}

export interface WirelessAcDomain {
  id: string
  displayName: string
  domainKind: string
  topologyStatus: string
  memberDeviceIds: string[]
  members: WirelessAcMember[]
  sourceEvidenceRefs: string[]
  siteScopeStatus: string
  revision: string
  asOf: number
}

export interface WirelessObservation {
  id: string
  resourceId: string
  sourceAcDeviceId: string
  sourceAcDomainId: string
  sourceSiteId: string
  observedApName: string
  serialEvidence?: string | null
  normalizedMacSet: string[]
  apModel?: string | null
  apGroup?: string | null
  rawState: string
  normalizedState: string
  stateConfidence: string
  capturedMs: number
  receivedMs: number
  rawManifestRef: string
  parserRevision: string
  sourceRevision: string
  relationStatus: string
}

export interface WirelessApResource {
  id: string
  displayName: string
  identityStatus: string
  serialEvidence?: string | null
  normalizedMacSet: string[]
  sourceObservationIds: string[]
  sourceAcDomainIds: string[]
  scopeStatus: string
  firstSeenMs: number
  lastSeenMs: number
  sourceRevision: string
  projectionRevision: string
  conflictReason?: string | null
  stateConflictReason?: string | null
  observations: WirelessObservation[]
}

export interface WirelessApGroup {
  id: string
  sourceAcDeviceId: string
  sourceAcDomainId: string
  sourceGroupKey: string | null
  displayName: string
  groupScope: 'AC' | 'DOMAIN' | 'UNKNOWN'
  groupIdentityKey: string | null
  groupIdentityStatus: 'CONFIRMED' | 'CANDIDATE' | 'CONFLICT' | 'UNKNOWN'
  memberResourceIds: string[]
  membershipRevision: string
  sourceObservationIds: string[]
  sourceAcDeviceIds: string[]
  sourceEvidenceRefs: string[]
  observedAtMs: number
  conflictReason: string | null
  sourceRevision: string
  scopeStatus: string
}

export interface WirelessApGroupDetail {
  group: WirelessApGroup
  members: WirelessApResource[]
  observations: WirelessObservation[]
  total: number
  asOf: number
  scope: WirelessScope
  sourceRevision: string
  warnings: string[]
  provenance: {
    sourceAcDeviceId: string
    sourceAcDomainId: string
    sourceRevision: string
    scopeStatus: string
    groupScope: WirelessApGroup['groupScope']
    sourceGroupKey: string | null
    groupIdentityKey: string | null
    groupIdentityStatus: WirelessApGroup['groupIdentityStatus']
    membershipRevision: string
    sourceObservationIds: string[]
    sourceAcDeviceIds: string[]
    sourceEvidenceRefs: string[]
    observedAtMs: number
    conflictReason: string | null
  }
}

export interface WirelessCollectionManifest {
  id: string
  sourceRevision: string
  scopeStatus: string
  sourceAcDeviceIds: string[]
  firstCapturedMs: number
  lastCapturedMs: number
  observationCount: number
  status: string
  createdMs: number
}

export interface WirelessCollectionManifestItem {
  observationId: string
  sourceAcDeviceId: string
  sourceAcDomainId: string
  sourceSiteId: string
  rawManifestRef: string
  sourceRevision: string
  capturedMs: number
  outcome: string
}

export interface WirelessCollectionManifestDetail {
  manifest: WirelessCollectionManifest
  items: WirelessCollectionManifestItem[]
  asOf: number
  scope: WirelessScope
  sourceRevision: string
  warnings: string[]
}

export interface WirelessPage<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
  hasMore: boolean
  asOf: number
  scope: WirelessScope
  sourceRevision: string
  warnings: string[]
}

export interface WirelessTopology {
  domainId: string
  domainKind: string
  topologyStatus: string
  memberDeviceIds: string[]
  sourceEvidenceRefs: string[]
  confirmed: boolean
  revision: string
  asOf: number
}

export interface WirelessApAssociation {
  resourceId: string
  displayName: string
  identityStatus: string
  observationId: string
  sourceAcDeviceId: string
  sourceAcDomainId: string
  sourceSiteId: string
  observedApName: string
  serialEvidence?: string | null
  normalizedMacSet: string[]
  apGroup?: string | null
  rawState: string
  normalizedState: string
  capturedMs: number
  rawManifestRef: string
  sourceRevision: string
  relationStatus: string
}

export interface WirelessEffectiveState {
  normalizedState: string
  rawState: string
  capturedMs: number
  sourceRevision: string
  rawManifestRef: string
  parserRevision: string
  verificationLevel: string
  freshnessState: string
}

export interface WirelessSourceRuntimeState {
  sourceAcDeviceId: string
  sourceSiteId: string
  normalizedState: string
  current: WirelessEffectiveState | null
  previousEffective: WirelessEffectiveState | null
  recent10: Array<{ id: string; previousState: string; normalizedState: string; capturedMs: number; sourceRevision: string; rawManifestRef: string }>
  latestOutcome: string
  latestCapturedMs: number | null
  freshnessDeadlineMs: number | null
  historyStatus: string
}

export interface WirelessRuntimeState {
  resourceId: string
  normalizedState: string
  sources: WirelessSourceRuntimeState[]
  scope: WirelessScope
  asOf: number
  sourceRevision: string
  freshnessWindowMs: number
  warnings: string[]
  current: WirelessAggregateEffectiveState | null
  previousEffective: WirelessAggregateEffectiveState | null
  recent10: Array<{ id: string; previousState: string; normalizedState: string; capturedMs: number; sourceRevision: string; sources: WirelessAggregateSourceEvidence[] }> | null
}

export interface WirelessAggregateSourceEvidence {
  sourceAcDeviceId: string
  sourceSiteId: string
  normalizedState: string
  capturedMs: number
  sourceRevision: string
  rawManifestRef: string
}

export interface WirelessAggregateEffectiveState {
  normalizedState: string
  capturedMs: number
  sources: WirelessAggregateSourceEvidence[]
  freshnessState: string
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
export function getModuleStates(): Promise<ModuleState[]> { return request('/api/system/modules') }
export function updateModuleState(key: string, enabled: boolean): Promise<ModuleState> {
  return request(`/api/system/modules/${encodeURIComponent(key)}`, { method: 'PUT', body: JSON.stringify({ enabled }) })
}
export function getDeviceInventory(params: { page?: number; pageSize?: number; search?: string; status?: string; siteId?: string; groupId?: string; vendor?: string; deviceType?: string } = {}): Promise<DeviceInventoryPage> {
  const query = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => { if (value !== undefined && value !== '') query.set(key, String(value)) })
  return request('/api/system/devices?' + query.toString())
}
export function getDeviceSites(): Promise<DeviceSite[]> { return request('/api/system/device-sites') }
export interface SiteSshJumpHost {
  siteId: string
  host: string
  port: number
  username: string
  enabled: boolean
  hostKeySha256: string
  credentialConfigured: boolean
  runtimeStatus: 'DISABLED' | 'ON_DEMAND' | 'CREDENTIAL_MISSING'
  updatedMs: number
}
export async function getSiteSshJumpHost(siteId: string): Promise<SiteSshJumpHost | null> {
  try { return await request(`/api/system/sites/${encodeURIComponent(siteId)}/ssh-jump-host`) }
  catch (error) { if (error instanceof ApiRequestError && error.status === 404) return null; throw error }
}
export function saveSiteSshJumpHost(siteId: string, payload: { host: string; port: number; username: string; enabled: boolean; password?: string; clearPassword?: boolean }): Promise<SiteSshJumpHost> {
  return request(`/api/system/sites/${encodeURIComponent(siteId)}/ssh-jump-host`, { method: 'PUT', body: JSON.stringify(payload) })
}
export function testSiteSshJumpHost(siteId: string): Promise<SiteSshJumpHost> {
  return request(`/api/system/sites/${encodeURIComponent(siteId)}/ssh-jump-host/test`, { method: 'POST', body: '{}' })
}
export function refreshSiteSshJumpHostFingerprint(siteId: string): Promise<SiteSshJumpHost> {
  return request(`/api/system/sites/${encodeURIComponent(siteId)}/ssh-jump-host/refresh-fingerprint`, { method: 'POST', body: '{}' })
}
export function clearSiteSshJumpHostFingerprint(siteId: string): Promise<SiteSshJumpHost> {
  return request(`/api/system/sites/${encodeURIComponent(siteId)}/ssh-jump-host/fingerprint`, { method: 'DELETE' })
}
export function getDeviceInventoryDetail(id: string): Promise<DeviceInventoryDetail> { return request('/api/system/devices/' + encodeURIComponent(id)) }
export function getZteProfiles(): Promise<ZteProfile[]> { return request('/api/system/zte/profiles') }
export function createZteProfile(payload: Record<string, unknown>): Promise<ZteProfile> {
  return request('/api/system/zte/profiles', { method: 'POST', body: JSON.stringify(payload) })
}
export function updateZteCredential(id: string, payload: { password?: string; clearPassword?: boolean }): Promise<ZteProfile> {
  return request(`/api/system/zte/profiles/${encodeURIComponent(id)}/credential`, { method: 'PUT', body: JSON.stringify(payload) })
}
export function startZteCollection(id: string): Promise<{ runId: string }> {
  return request(`/api/system/zte/profiles/${encodeURIComponent(id)}/collect`, { method: 'POST', body: JSON.stringify({}) })
}
export function getZteRun(id: string): Promise<ZteCollectionRun> { return request(`/api/system/zte/runs/${encodeURIComponent(id)}`) }
export function getZteFacts(id: string, params: { kind?: string; history?: boolean; limit?: number } = {}): Promise<ZteFact[]> {
  return request(queryPath(`/api/system/zte/profiles/${encodeURIComponent(id)}/facts`, params))
}
export function getH3cFacts(id: string, params: { kind?: string; history?: boolean; limit?: number } = {}): Promise<H3cFact[]> {
  return request(queryPath(`/api/system/h3c/profiles/${encodeURIComponent(id)}/facts`, params))
}
export function getH3cLldpRelations(id: string, params: { history?: boolean; limit?: number } = {}): Promise<H3cLldpRelation[]> {
  return request('/api/system/h3c/profiles/' + encodeURIComponent(id) + '/lldp-relations?' + new URLSearchParams(Object.entries(params).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString())
}
export function getH3cLldpClaims(id: string, params: { history?: boolean; limit?: number } = {}): Promise<H3cLldpClaims> {
  return request('/api/system/h3c/profiles/' + encodeURIComponent(id) + '/lldp-claims?' + new URLSearchParams(Object.entries(params).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)])).toString())
}
export function createConfigBackup(profileIds: string[], configurationKind: ConfigBackupKind = 'RUNNING'): Promise<ConfigBackupBatch> {
  return request(configurationKind === 'STARTUP_BODY' ? '/api/system/config-backups/startup-body' : '/api/system/config-backups', {
    method: 'POST', body: JSON.stringify(configurationKind === 'STARTUP_BODY' ? { profileIds } : { profileIds, configurationKind })
  })
}
export function getConfigBackupBatch(id: string): Promise<ConfigBackupBatch> {
  return request('/api/system/config-backups/batches/' + encodeURIComponent(id))
}
export function getConfigBackups(profileId: string, configurationKind?: ConfigBackupKind): Promise<ConfigBackupSnapshot[]> {
  return request('/api/system/config-backups?profileId=' + encodeURIComponent(profileId) +
    (configurationKind ? '&configurationKind=' + encodeURIComponent(configurationKind) : ''))
}
export async function downloadConfigBackup(id: string): Promise<Blob> {
  const response = await fetch('/api/system/config-backups/' + encodeURIComponent(id) + '/download', { credentials: 'include' })
  if (!response.ok) throw new ApiRequestError(response.status, await response.text())
  return response.blob()
}
export function getConfigBackupDiff(baseId: string, targetId: string): Promise<ConfigBackupDiff> {
  return request('/api/system/config-backups/' + encodeURIComponent(baseId) + '/diff/' + encodeURIComponent(targetId))
}
export function getConfigBackupSchedules(profileId?: string): Promise<ConfigBackupSchedule[]> {
  return request('/api/system/config-backup-schedules' + (profileId ? '?profileId=' + encodeURIComponent(profileId) : ''))
}
export function createConfigBackupSchedule(payload: { profileIds: string[]; configurationKind: ConfigBackupKind; intervalMinutes: number }): Promise<ConfigBackupSchedule> {
  return request('/api/system/config-backup-schedules', { method: 'POST', body: JSON.stringify(payload) })
}
export function setConfigBackupScheduleEnabled(id: string, enabled: boolean): Promise<ConfigBackupSchedule> {
  return request('/api/system/config-backup-schedules/' + encodeURIComponent(id), { method: 'PATCH', body: JSON.stringify({ enabled }) })
}
export function deleteConfigBackupSchedule(id: string): Promise<{ deleted: boolean }> {
  return request('/api/system/config-backup-schedules/' + encodeURIComponent(id), { method: 'DELETE' })
}
export function getWirelessAcDomains(params: { page?: number; pageSize?: number; sourceAcId?: string } = {}): Promise<WirelessPage<WirelessAcDomain>> {
  return request(queryPath('/api/system/wireless/ac-domains', params))
}
export function getWirelessApResources(params: { page?: number; pageSize?: number; serial?: string; mac?: string; domainId?: string; sourceAcId?: string; search?: string } = {}): Promise<WirelessPage<WirelessApResource>> {
  return request(queryPath('/api/system/wireless/ap-resources', params))
}
export function getWirelessApResource(id: string): Promise<WirelessApResource> {
  return request(`/api/system/wireless/ap-resources/${encodeURIComponent(id)}`)
}
export function getWirelessRuntimeState(id: string, params: { sourceAcId?: string; domainId?: string } = {}, signal?: AbortSignal): Promise<WirelessRuntimeState> {
  return request(queryPath(`/api/system/wireless/ap-resources/${encodeURIComponent(id)}/runtime-state`, params), { signal })
}
export function getWirelessApObservations(id: string, params: { page?: number; pageSize?: number; sourceAcId?: string; domainId?: string } = {}): Promise<WirelessPage<WirelessObservation>> {
  return request(queryPath(`/api/system/wireless/ap-resources/${encodeURIComponent(id)}/observations`, params))
}
export function getWirelessTopology(domainId: string): Promise<WirelessTopology> {
  return request(`/api/system/wireless/ac-domains/${encodeURIComponent(domainId)}/topology`)
}
export function getWirelessApAssociations(domainId: string, params: { sourceAcId?: string } = {}): Promise<{ items: WirelessApAssociation[]; total: number; domainId: string; asOf: number; scope: WirelessScope; sourceRevision: string; warnings: string[] }> {
  return request(queryPath(`/api/system/wireless/ac-domains/${encodeURIComponent(domainId)}/ap-associations`, params))
}
export function getWirelessCollectionManifests(domainId: string, params: { page?: number; pageSize?: number; sourceAcId?: string } = {}): Promise<WirelessPage<WirelessCollectionManifest>> {
  return request(queryPath(`/api/system/wireless/ac-domains/${encodeURIComponent(domainId)}/collection-manifests`, params))
}
export function getWirelessCollectionManifest(id: string, params: { sourceAcId?: string } = {}): Promise<WirelessCollectionManifestDetail> {
  return request(queryPath(`/api/system/wireless/collection-manifests/${encodeURIComponent(id)}`, params))
}
export function getWirelessApGroups(domainId: string, params: { page?: number; pageSize?: number; sourceAcId?: string } = {}): Promise<WirelessPage<WirelessApGroup>> {
  return request(queryPath(`/api/system/wireless/ac-domains/${encodeURIComponent(domainId)}/ap-groups`, params))
}
export function getWirelessApGroup(id: string, params: { sourceAcId?: string } = {}): Promise<WirelessApGroupDetail> {
  return request(queryPath(`/api/system/wireless/ap-groups/${encodeURIComponent(id)}`, params))
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
export function getTask(runId: string): Promise<TaskRun> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}`)
}
export function createDeviceConnectionTests(deviceIds: string[]): Promise<H3cTaskPlan> {
  return request('/api/system/devices/connection-tests', { method: 'POST', body: JSON.stringify({ deviceIds }) })
}
export function getH3cTaskPlan(id: string): Promise<H3cTaskPlan> {
  return request('/api/system/h3c/task-plans/' + encodeURIComponent(id))
}
export function getH3cTaskPlanReport(id: string): Promise<H3cTaskPlanReport> {
  return request('/api/system/h3c/task-plans/' + encodeURIComponent(id) + '/report')
}
export function cancelH3cTaskPlan(id: string): Promise<H3cTaskPlan> {
  return request('/api/system/h3c/task-plans/' + encodeURIComponent(id) + '/cancel', { method: 'POST' })
}
export interface H3cTaskDefinitionRequest {
  id: string
  profileId: string
  command: string
  commands?: string[]
  timeoutMs?: number
  enabled?: boolean
}
export function createH3cTaskDefinition(payload: H3cTaskDefinitionRequest): Promise<Record<string, unknown>> {
  return request('/api/system/h3c/task-definitions', { method: 'POST', body: JSON.stringify(payload) })
}
export function startH3cTaskDefinition(definitionId: string): Promise<{ runId: string }> {
  return request(`/api/system/tasks/${encodeURIComponent(definitionId)}/runs`, { method: 'POST', body: JSON.stringify({}) })
}
export function getTaskCommands(runId: string, signal?: AbortSignal): Promise<TaskCommand[]> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}/commands`, { signal })
}
export function getTaskDiagnostics(runId: string, signal?: AbortSignal): Promise<TaskDiagnosticManifest> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}/diagnostics`, { signal, cache: 'no-store' })
}
export async function downloadTaskDiagnostics(runId: string, commandIndex?: number, signal?: AbortSignal): Promise<Blob> {
  const query = commandIndex === undefined ? '' : '?' + new URLSearchParams({ commandIndex: String(commandIndex) })
  const response = await fetch(`/api/system/tasks/${encodeURIComponent(runId)}/diagnostics/download${query}`, { credentials: 'include', cache: 'no-store', signal })
  if (!response.ok) throw new ApiRequestError(response.status, await response.text())
  return response.blob()
}
export function cancelTask(runId: string): Promise<{ cancelled: boolean; runId: string }> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}/cancel`, { method: 'POST' })
}
export function retryTask(runId: string): Promise<{ runId: string; previousRunId: string; action: string }> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}/retry`, { method: 'POST' })
}
export function resumeTask(runId: string): Promise<{ runId: string; previousRunId: string; action: string }> {
  return request(`/api/system/tasks/${encodeURIComponent(runId)}/resume`, { method: 'POST' })
}
export function startNetworkDiagnostics(payload: { deviceIds: string[]; kinds: NetworkDiagnosticKind[]; timeoutMs?: number; siteId?: string }): Promise<NetworkDiagnosticRun> {
  return request('/api/system/diagnostics/runs', { method: 'POST', body: JSON.stringify(payload) })
}
export function getNetworkDiagnostics(id: string): Promise<NetworkDiagnosticRun> {
  return request(`/api/system/diagnostics/runs/${encodeURIComponent(id)}`)
}
export function listNetworkDiagnostics(limit = 50): Promise<NetworkDiagnosticRunSummary[]> {
  return request('/api/system/diagnostics/runs?limit=' + encodeURIComponent(String(limit)))
}
export function cancelNetworkDiagnostics(id: string): Promise<{ cancelled: boolean }> {
  return request(`/api/system/diagnostics/runs/${encodeURIComponent(id)}/cancel`, { method: 'POST' })
}
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
export interface H3cOnboardRequest {
  host: string
  siteId?: string
  protocol?: 'SSH' | 'TELNET' | 'AUTO'
  port?: number
  username: string
  credentialReference?: string
  password?: string
  allowTelnetFallback?: boolean
  inventory?: Record<string, unknown>
}
export function onboardH3cProfile(payload: H3cOnboardRequest): Promise<TerminalDevice> {
  return request('/api/system/h3c/profiles/onboard', { method: 'POST', body: JSON.stringify(payload) })
}
export function testH3cConnection(profileId: string): Promise<H3cConnectionTestResult> {
  return request(`/api/system/h3c/profiles/${encodeURIComponent(profileId)}/test-connection`, { method: 'POST' })
}
export function updateH3cCredential(profileId: string, payload: { password?: string; clearPassword?: boolean }): Promise<TerminalDevice> {
  return request(`/api/system/h3c/profiles/${encodeURIComponent(profileId)}/credential`, { method: 'PUT', body: JSON.stringify(payload) })
}

export function createTerminalSession(payload: Record<string, unknown>): Promise<{ sessionId: string }> {
  return request('/api/system/terminal/sessions', { method: 'POST', body: JSON.stringify(payload) })
}
export function getLinuxServers(): Promise<LinuxServer[]> { return request('/api/system/linux/servers') }
export function createLinuxServer(payload: LinuxServerWrite): Promise<LinuxServer> {
  return request('/api/system/linux/servers', { method: 'POST', body: JSON.stringify(payload) })
}
export function updateLinuxServer(id: string, payload: LinuxServerWrite): Promise<LinuxServer> {
  return request(`/api/system/linux/servers/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(payload) })
}
export function deleteLinuxServer(id: string): Promise<void> {
  return request(`/api/system/linux/servers/${encodeURIComponent(id)}`, { method: 'DELETE' })
}
export function testLinuxServer(id: string): Promise<{ status: string; message: string }> {
  return request(`/api/system/linux/servers/${encodeURIComponent(id)}/test`, { method: 'POST', body: '{}' })
}
export function createLinuxServerSession(id: string): Promise<{ sessionId: string; mode: 'LINUX_INTERACTIVE' }> {
  return request(`/api/system/linux/servers/${encodeURIComponent(id)}/sessions`, { method: 'POST', body: '{}' })
}
export function issueTerminalTicket(sessionId: string): Promise<{ ticket: string; expiresAtMs: number }> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/ticket`, { method: 'POST' })
}
export function closeTerminalSession(sessionId: string): Promise<void> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}/close`, { method: 'POST' })
}
export function getTerminalSessions(query: TerminalSessionQuery = {}): Promise<TerminalSessionPage> {
  const params = new URLSearchParams({ page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 20) })
  if (query.keyword?.trim()) params.set('keyword', query.keyword.trim())
  if (query.protocol) params.set('protocol', query.protocol)
  return request(`/api/system/terminal/sessions?${params}`)
}
export function getTerminalSession(sessionId: string): Promise<TerminalSession> {
  return request(`/api/system/terminal/sessions/${encodeURIComponent(sessionId)}`)
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

