import type { WirelessApResource } from './client'

export function wirelessObservationsCsv(resources: readonly WirelessApResource[], context: {
  domainId: string; acDeviceId: string; search: string; stateFilter: string; exportedMs: number; partial: boolean
}): string {
  const rows: Array<Array<string | number>> = [[
    'resource_id', 'display_name', 'identity_status', 'serial', 'mac_set', 'observation_id',
    'source_site_id', 'source_ac_domain_id', 'source_ac_device_id', 'observed_ap_name', 'ap_group',
    'raw_state', 'normalized_state', 'captured_utc', 'raw_manifest_ref', 'source_revision',
    'projection_revision', 'scope_status', 'filter_domain_id', 'filter_ac_device_id',
    'filter_search', 'filter_state', 'partial_load', 'exported_utc', 'export_scope',
  ]]
  for (const resource of resources) {
    const observations = resource.observations.filter(item =>
      (!context.domainId || item.sourceAcDomainId === context.domainId) &&
      (!context.acDeviceId || item.sourceAcDeviceId === context.acDeviceId))
    for (const item of observations) rows.push([
      resource.id, resource.displayName, resource.identityStatus, resource.serialEvidence ?? '',
      JSON.stringify(resource.normalizedMacSet), item.id, item.sourceSiteId, item.sourceAcDomainId,
      item.sourceAcDeviceId, item.observedApName, item.apGroup ?? '', item.rawState, item.normalizedState,
      new Date(item.capturedMs).toISOString(), item.rawManifestRef, item.sourceRevision,
      resource.projectionRevision, resource.scopeStatus, context.domainId, context.acDeviceId,
      context.search, context.stateFilter, String(context.partial), new Date(context.exportedMs).toISOString(),
      'FILTERED_LOADED_OBSERVATIONS_ONLY',
    ])
  }
  const cell = (value: string | number) => {
    const text = String(value)
    const safe = /^[\s\u0000-\u001f]*[=+\-@]/.test(text) || /^[\t\r\n]/.test(text) ? "'" + text : text
    return '"' + safe.replaceAll('"', '""') + '"'
  }
  return '\uFEFF' + rows.map(row => row.map(cell).join(',')).join('\r\n') + '\r\n'
}
