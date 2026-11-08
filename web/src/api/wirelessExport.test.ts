import { describe, expect, it } from 'vitest'
import type { WirelessApResource, WirelessObservation } from './client'
import { wirelessObservationsCsv } from './wirelessExport'

const observation = (id: string, ac: string, state: string, changes = {}): WirelessObservation => ({
  id, resourceId: 'physical-ap', sourceAcDeviceId: ac, sourceAcDomainId: 'domain', sourceSiteId: 'site-a',
  observedApName: 'AP', normalizedMacSet: ['001122334455'], rawState: state, normalizedState: state,
  capturedMs: 1000, receivedMs: 1000, rawManifestRef: `raw-${id}`, parserRevision: 'parser-1',
  sourceRevision: `revision-${id}`, stateConfidence: 'HIGH', relationStatus: 'OBSERVED', ...changes,
})
const resource: WirelessApResource = {
  id: 'physical-ap', displayName: 'AP', identityStatus: 'CONFIRMED', serialEvidence: 'serial',
  normalizedMacSet: ['001122334455'], sourceObservationIds: ['a', 'b'], sourceAcDomainIds: ['domain'],
  scopeStatus: 'FULL', firstSeenMs: 1000, lastSeenMs: 1000, sourceRevision: 'source', projectionRevision: 'projection',
  observations: [observation('a', 'ac-a', 'R/M'), observation('b', 'ac-b', 'R/B')],
}
const context = { domainId: 'domain', acDeviceId: '', search: '', stateFilter: '', exportedMs: 2000, partial: false }

describe('wireless observation export', () => {
  it('keeps both AC observations and their separate Raw references for one physical AP', () => {
    const csv = wirelessObservationsCsv([resource], context)
    expect(csv.split('\r\n')).toHaveLength(4)
    expect(csv).toContain('"ac-a"')
    expect(csv).toContain('"ac-b"')
    expect(csv).toContain('"raw-a"')
    expect(csv).toContain('"raw-b"')
    expect(csv).toContain('"R/M"')
    expect(csv).toContain('"R/B"')
  })
  it('exports only the selected exact AC and Domain observations', () => {
    const csv = wirelessObservationsCsv([resource], { ...context, acDeviceId: 'ac-a' })
    expect(csv).toContain('"raw-a"')
    expect(csv).not.toContain('"raw-b"')
    expect(wirelessObservationsCsv([resource], { ...context, domainId: 'other-domain' }).split('\r\n')).toHaveLength(2)
  })
  it('retains stale and conflicting source evidence without inferring HA or recovery', () => {
    const csv = wirelessObservationsCsv([{ ...resource, identityStatus: 'CONFLICT',
      observations: [observation('a', 'ac-a', 'STALE')] }], { ...context, partial: true, stateFilter: 'STALE' })
    expect(csv).toContain('"CONFLICT"')
    expect(csv).toContain('"STALE"')
    expect(csv).toContain('"true"')
    expect(csv).toContain('FILTERED_LOADED_OBSERVATIONS_ONLY')
  })
  it('escapes spreadsheet formulas, quotes and multiline names without dropping provenance', () => {
    const csv = wirelessObservationsCsv([{ ...resource, displayName: '=SUM(1,2)',
      observations: [observation('a', 'ac-a', 'UNKNOWN', { observedApName: 'AP "one"\nline' })] }], context)
    expect(csv).toContain('"\'=SUM(1,2)"')
    expect(csv).toContain('"AP ""one""\nline"')
    expect(csv).toContain('"projection"')
  })
})
