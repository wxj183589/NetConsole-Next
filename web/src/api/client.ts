export interface WebUser {
  username: string
  role: 'ADMIN' | 'SUPERADMIN'
}

export interface HealthSnapshot {
  status: string
  serviceState: string
  healthState: string
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
  resources?: { workingSetBytes: number; threadCount: number }
  instanceId?: string
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

