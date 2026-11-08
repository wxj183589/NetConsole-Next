import type { WirelessApResource, WirelessRuntimeState } from './client'

// The API has already applied Site claims and AC/Domain scope. Keep its bounded history explicit.
export function wirelessHistoryCsv(resource: WirelessApResource, runtime: WirelessRuntimeState, context: {
  domainId: string; acDeviceId: string; exportedMs: number
}): string {
  if (resource.id !== runtime.resourceId) throw new Error('AP_HISTORY_RESOURCE_MISMATCH')
  const rows: unknown[][] = [[
    'resource_id', 'serial', 'mac_set', 'record_type', 'change_id', 'previous_state', 'normalized_state',
    'captured_utc', 'source_ac_device_id', 'source_site_id', 'raw_state', 'raw_manifest_ref', 'source_revision',
    'aggregate_source_evidence_json', 'freshness_state', 'verification_level', 'parser_revision', 'latest_collection_outcome',
    'latest_collection_utc', 'history_status', 'runtime_state', 'runtime_revision', 'scope_status',
    'claims_revision', 'warnings_json', 'freshness_window_ms', 'as_of_utc', 'filter_domain_id', 'filter_ac_device_id', 'exported_utc', 'export_scope',
  ]]
  const iso = (value: number | null | undefined) => value == null ? '' : new Date(value).toISOString()
  const append = (record: Record<string, unknown>) => rows.push([
    resource.id, resource.serialEvidence || '', JSON.stringify(resource.normalizedMacSet),
    record.type, record.id || '', record.previous || '', record.state || '', iso(record.captured as number | undefined),
    record.ac || '', record.site || '', record.rawState || '', record.raw || '', record.revision || '',
    record.evidence ? JSON.stringify(record.evidence) : '', record.freshness || '', record.verification || '',
    record.parser || '', record.outcome || '', iso(record.latest as number | null | undefined), record.history || '',
    runtime.normalizedState, runtime.sourceRevision, runtime.scope.status, runtime.scope.claimsRevision,
    JSON.stringify(runtime.warnings), runtime.freshnessWindowMs, iso(runtime.asOf), context.domainId, context.acDeviceId, iso(context.exportedMs),
    'LOADED_SCOPED_CURRENT_PREVIOUS_RECENT10_ONLY',
  ])
  append({ type: 'RUNTIME_STATUS', state: runtime.normalizedState })
  for (const [type, value] of [['AGGREGATE_CURRENT', runtime.current], ['AGGREGATE_PREVIOUS', runtime.previousEffective]] as const) {
    if (value) append({ type, state: value.normalizedState, captured: value.capturedMs, evidence: value.sources, freshness: value.freshnessState })
  }
  for (const change of runtime.recent10 || []) append({ type: 'AGGREGATE_CHANGE', id: change.id, previous: change.previousState,
    state: change.normalizedState, captured: change.capturedMs, revision: change.sourceRevision, evidence: change.sources })
  for (const source of runtime.sources) {
    const metadata = { ac: source.sourceAcDeviceId, site: source.sourceSiteId, outcome: source.latestOutcome,
      latest: source.latestCapturedMs, history: source.historyStatus }
    append({ ...metadata, type: 'SOURCE_STATUS', state: source.normalizedState })
    for (const [type, value] of [['SOURCE_CURRENT', source.current], ['SOURCE_PREVIOUS', source.previousEffective]] as const) {
      if (value) append({ ...metadata, type, state: value.normalizedState, rawState: value.rawState, captured: value.capturedMs,
        raw: value.rawManifestRef, revision: value.sourceRevision, freshness: value.freshnessState, verification: value.verificationLevel, parser: value.parserRevision })
    }
    for (const change of source.recent10) append({ ...metadata, type: 'SOURCE_CHANGE', id: change.id, previous: change.previousState,
      state: change.normalizedState, captured: change.capturedMs, raw: change.rawManifestRef, revision: change.sourceRevision })
  }
  const cell = (value: unknown) => {
    const text = String(value ?? '')
    const safe = /^[\s\p{Cc}\p{Cf}]*[=+@-]/u.test(text) ? "'" + text : text
    return '"' + safe.replaceAll('"', '""') + '"'
  }
  return '\uFEFF' + rows.map(row => row.map(cell).join(',')).join('\r\n') + '\r\n'
}
