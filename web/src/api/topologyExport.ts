import type { H3cLldpRelation } from './client'

export interface TopologyRelation extends H3cLldpRelation {
  profileHost: string
  scopeState: string
  claimsRevision: string
}

function csvCell(value: string | number): string {
  const text = String(value)
  // Neutralize spreadsheet formulas even when preceded by whitespace/control
  // characters. Quote all cells to preserve exact commas, quotes and newlines.
  const safe = /^[\s\u0000-\u001f]*[=+\-@]/.test(text) || /^[\t\r\n]/.test(text) ? "'" + text : text
  return '"' + safe.replaceAll('"', '""') + '"'
}

export function topologyRelationsCsv(rows: readonly TopologyRelation[], context: {
  loadedMs: number; exportedMs: number; failedProfiles: readonly string[]; potentiallyTruncatedProfiles: readonly string[]
}): string {
  const header = ['profile_id', 'profile_host', 'local_device_id', 'local_interface', 'neighbor_device', 'neighbor_port',
    'neighbor_management_address', 'neighbor_chassis_id', 'site_id', 'captured_utc', 'source', 'fact_revision',
    'relation_key', 'change_kind', 'scope_state', 'claims_revision', 'loaded_utc', 'exported_utc',
    'partial_failed_profiles', 'limit_reached_profiles', 'export_scope', 'source_authority', 'captured_scope_key']
  const lines: Array<Array<string | number>> = [header]
  for (const row of rows) lines.push([row.profileId, row.profileHost, row.localDeviceId, row.localInterface,
    row.neighborDevice, row.neighborPort ?? '', row.neighborManagementAddress ?? '', row.neighborChassisId ?? '', row.siteId,
    row.capturedMs ? new Date(row.capturedMs).toISOString() : '', row.source, row.factRevision, row.relationKey,
    row.changeKind, row.scopeState, row.claimsRevision, new Date(context.loadedMs).toISOString(),
    new Date(context.exportedMs).toISOString(), JSON.stringify(context.failedProfiles),
    JSON.stringify(context.potentiallyTruncatedProfiles), 'CURRENT_FILTER_LOADED_CLAIMS_ONLY', row.sourceAuthority ?? '', row.capturedScopeKey ?? ''])
  return '\uFEFF' + lines.map(line => line.map(csvCell).join(',')).join('\r\n') + '\r\n'
}
