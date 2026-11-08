import { request } from './client'

export interface RestorePreflight {
  id: string; batchId: string; profileId: string; backupId: string
  state: string; createdMs: number; completedMs?: number | null; elapsedMs: number
  sourceIntegrityVerified: boolean; deviceIdentityVerified: boolean; independentReadbackStable: boolean
  businessBaselineStable: boolean; sourceMatchesCurrent: boolean
  sourceSha256?: string | null; firstBackupId?: string | null; secondBackupId?: string | null
  firstSha256?: string | null; secondSha256?: string | null; businessBaselineSha256?: string | null
  capabilities?: { nativeFileReplaceAdvertised: boolean; sftpAvailable: boolean; startupFileBytes?: number | null; transferState: string } | null
  checkpointState: string; blockers: string[]
  phases: Array<{ name: string; state: string; elapsedMs: number; error?: string | null }>
  error?: string | null
  writeAllowed: boolean; restorable: boolean; writePass: boolean; readbackPass: boolean
  configHashPass: boolean; businessProbePass: boolean; rollbackPass: boolean
}
export interface RestorePreflightBatch { id: string; state: string; createdMs: number; completedMs: number; items: RestorePreflight[] }
export function checkRestoreReadiness(sources: Array<{ profileId: string; backupId: string }>): Promise<RestorePreflightBatch> {
  return request('/api/system/simulator-config/restore-preflight', { method: 'POST', body: JSON.stringify({ sources }) })
}
export function getRestoreReadinessHistory(profileId: string): Promise<RestorePreflight[]> {
  return request('/api/system/simulator-config/restore-preflight?profileId=' + encodeURIComponent(profileId))
}
