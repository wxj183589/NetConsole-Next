import { request } from './client'

export interface FaultTransition { atMs: number; action: string; actorUserId: string; evidenceId: string; detail: string }
export interface FaultIncident {
  id: string; deviceId: string; profileId: string; siteId: string; kind: string; resourceKey: string
  severity: string; state: string; firstSeenMs: number; lastSeenMs: number; evidenceType: string
  evidenceId: string; detail: string; occurrences: number; history: FaultTransition[]
}
export interface FaultPolicy {
  deviceId: string; faultId: string; enabled: boolean; intervalMinutes: number; maximumAttempts: number
  attempts: number; nextRunMs: number | null; state: string; planId: string | null; recoveryId: string | null; lastError: string | null
}
export interface FaultResult { page: number; pageSize: number; total: number; hasMore: boolean; items: FaultIncident[]; policies: FaultPolicy[]; limitations: string[]; summary?: { open: number; acknowledged: number; processing: number; resolved: number; reopened: number; affectedDevices: number } }
export interface FaultFilters { state?: string; deviceId?: string; kind?: string; fromMs?: number; toMs?: number }
const root = '/api/system/operations/faults'
export function getFaults(page = 1, filters: FaultFilters = {}) {
  const query = new URLSearchParams({ page: String(page) })
  Object.entries(filters).forEach(([key, value]) => { if (value !== '' && value != null) query.set(key, String(value)) })
  return request<FaultResult>(`${root}?${query}`)
}
export const syncFaults = (page = 1) => request(`${root}/sync?page=${page}`, { method: 'POST' })
export const acknowledgeFault = (id: string) => request<FaultIncident>(`${root}/${encodeURIComponent(id)}/ack`, { method: 'POST' })
export const processFault = (id: string) => request<FaultIncident>(`${root}/${encodeURIComponent(id)}/process`, { method: 'POST' })
export const verifyFault = (id: string) => request<{ state: string; detail: string; incident: FaultIncident }>(`${root}/${encodeURIComponent(id)}/verify`, { method: 'POST' })
export const setFaultPolicy = (item: FaultIncident, enabled: boolean, intervalMinutes = 5, maximumAttempts = 3) =>
  request<FaultPolicy>(`${root}/policies/${encodeURIComponent(item.deviceId)}`, { method: 'PUT', body: JSON.stringify({ faultId: item.id, expectedProfileId: item.profileId, enabled, intervalMinutes, maximumAttempts }) })
