import { describe, expect, it } from 'vitest'
import type { WirelessApResource, WirelessRuntimeState } from './client'
import { wirelessHistoryCsv } from './wirelessHistoryExport'

const resource: WirelessApResource = { id: 'physical-ap', displayName: 'AP', identityStatus: 'CONFIRMED',
  serialEvidence: 'serial', normalizedMacSet: ['001122334455'], sourceObservationIds: [], sourceAcDomainIds: ['domain'],
  scopeStatus: 'FULL', firstSeenMs: 1000, lastSeenMs: 2000, sourceRevision: 'r1', projectionRevision: 'p1', observations: [] }
const evidence = (ac: string) => ({ sourceAcDeviceId: ac, sourceSiteId: 'site', normalizedState: 'RUNNING_MASTER', capturedMs: 1000, sourceRevision: 'revision-' + ac, rawManifestRef: 'raw-' + ac })
const runtime = (): WirelessRuntimeState => ({ resourceId: 'physical-ap', normalizedState: 'STALE',
  scope: { status: 'PARTIAL', claimsRevision: 'claims-r2' }, asOf: 2000, sourceRevision: 'runtime-r3', freshnessWindowMs: 1000,
  warnings: ['PARTIAL_SCOPE', 'OFFLINE_FIXTURE_EVIDENCE'],
  current: { normalizedState: 'RUNNING_MASTER', capturedMs: 1000, sources: [evidence('ac-a'), evidence('ac-b')], freshnessState: 'STALE' },
  previousEffective: null,
  recent10: [{ id: 'aggregate-change', previousState: 'UNKNOWN', normalizedState: 'RUNNING_MASTER', capturedMs: 1000, sourceRevision: 'aggregate-r1', sources: [evidence('ac-a'), evidence('ac-b')] }],
  sources: ['ac-a', 'ac-b'].map(ac => ({ sourceAcDeviceId: ac, sourceSiteId: 'site', normalizedState: 'STALE',
    current: { normalizedState: ac === 'ac-a' ? 'RUNNING_MASTER' : 'RUNNING_BACKUP', rawState: ac === 'ac-a' ? 'R/M' : 'R/B', capturedMs: 1000, sourceRevision: 'revision-' + ac, rawManifestRef: 'raw-' + ac, parserRevision: 'parser-r1', verificationLevel: 'SIMULATOR_READONLY', freshnessState: 'STALE' },
    previousEffective: null, recent10: [{ id: 'change-' + ac, previousState: 'UNKNOWN', normalizedState: ac === 'ac-a' ? 'RUNNING_MASTER' : 'RUNNING_BACKUP', capturedMs: 1000, sourceRevision: 'revision-' + ac, rawManifestRef: 'raw-' + ac }],
    latestOutcome: 'COLLECTION_FAILED', latestCapturedMs: 1500, freshnessDeadlineMs: 2000, historyStatus: 'RECENT10_PARTIAL' })),
})
const context = { domainId: 'domain', acDeviceId: '', exportedMs: 3000 }

describe('bounded AP effective history CSV', () => {
  it('retains independent AC evidence and failed collection without inventing offline transitions', () => {
    const csv = wirelessHistoryCsv(resource, runtime(), context)
    expect(csv).toContain('"AGGREGATE_CHANGE","aggregate-change"')
    expect(csv).toContain('"SOURCE_CHANGE","change-ac-a"')
    expect(csv).toContain('"SOURCE_CHANGE","change-ac-b"')
    expect(csv).toContain('"R/M"'); expect(csv).toContain('"R/B"')
    expect(csv).toContain('"raw-ac-a"'); expect(csv).toContain('"raw-ac-b"')
    expect(csv).toContain('"COLLECTION_FAILED"'); expect(csv).toContain('"STALE"')
    expect(csv).not.toContain('"OFFLINE"')
    expect(csv).toContain('"SIMULATOR_READONLY","parser-r1"')
  })
  it('preserves query scope, incomplete history and evidence warnings', () => {
    const csv = wirelessHistoryCsv(resource, runtime(), context)
    expect(csv).toContain('"RECENT10_PARTIAL"')
    expect(csv).toContain('"PARTIAL","claims-r2"')
    expect(csv).toContain('OFFLINE_FIXTURE_EVIDENCE')
    expect(csv).toContain('LOADED_SCOPED_CURRENT_PREVIOUS_RECENT10_ONLY')
    expect(csv).toContain('"1970-01-01T00:00:02.000Z","domain","","1970-01-01T00:00:03.000Z"')
    expect(csv).not.toContain('REAL_HA')
  })
  it('exports exactly the API scoped history and rejects a different selected AP', () => {
    const data = runtime(); data.sources = [data.sources[0]!]; data.current = null; data.recent10 = null
    const csv = wirelessHistoryCsv(resource, data, { ...context, acDeviceId: 'ac-a' })
    expect(csv).toContain('"ac-a"'); expect(csv).not.toContain('ac-b')
    expect(() => wirelessHistoryCsv({ ...resource, id: 'other-ap' }, data, context)).toThrow('AP_HISTORY_RESOURCE_MISMATCH')
  })
  it('escapes formula-like identity and quoted multiline evidence', () => {
    const data = runtime(); data.sources[0]!.current!.rawManifestRef = 'raw "one"\nline'
    const csv = wirelessHistoryCsv({ ...resource, serialEvidence: '=SUM(1,2)' }, data, { ...context, domainId: '@domain' })
    expect(csv).toContain(String.fromCharCode(34, 39) + '=SUM(1,2)"')
    expect(csv).toContain(String.fromCharCode(34, 39) + '@domain"')
    expect(csv).toContain('"raw ""one""\nline"')
  })
})
