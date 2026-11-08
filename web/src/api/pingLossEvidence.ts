import { request } from './client'

export interface PingLossFactEvidence {
  id: string; kind: string; identityKey: string; status: string; source: string; capturedScopeKey: string
  capturedMs: number; collectionEndedMs: number; ageMs: number
  observedValues: Record<string, string>
  rawReferences: Array<{ runId: string; command: string; outputSha256: string; segmentSha256: string }>
}
export interface PingLossEvidenceSnapshot {
  runId: string; itemId: string; deviceId: string; siteId: string; sequence: number
  sampleCapturedMs: number; sampleStatus: string; destinationAddress: string; protocol: string; port: number | null
  state: 'HISTORICAL_CONTEXT' | 'TARGET_BINDING_UNPROVEN' | 'NO_PRIOR_VERIFIED_EVIDENCE'
  connectedApState: 'UNKNOWN'; connectedApReason: string; facts: PingLossFactEvidence[]; hasMore: boolean; limitations: string[]
}
export const getPingLossEvidence = (runId: string, itemId: string, sequence: number) =>
  request<PingLossEvidenceSnapshot>(`/api/system/diagnostics/runs/${encodeURIComponent(runId)}/items/${encodeURIComponent(itemId)}/loss-evidence/${sequence}`)
