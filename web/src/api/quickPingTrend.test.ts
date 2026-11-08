import { describe, expect, it } from 'vitest'
import type { NetworkDiagnosticRun, QuickPingSnapshot } from './client'
import { buildQuickPingTrends, quickPingTrendCsv } from './quickPingTrend'

export function run(id: string, values: Array<number | null>, changes: Partial<NetworkDiagnosticRun> = {}, pingChanges: Partial<QuickPingSnapshot> = {}): NetworkDiagnosticRun {
  const received = values.filter(value => value !== null).length
  return { id, actor: 'actor-a', state: 'SUCCEEDED', createdMs: 1000, items: [{
    id: 'item-' + id, kind: 'QUICK_PING', deviceId: 'd1', deviceName: 'Device', siteId: 's1', state: 'SUCCEEDED', endedMs: 2000,
    quickPing: { protocol: 'TCP', destinationAddress: '192.0.2.1', port: 22, requestedSamples: values.length,
      sent: values.length, received, lost: values.length - received,
      samples: values.map((value, index) => ({ sequence: index + 1, capturedMs: 1000 + index,
        status: value === null ? 'TIMED_OUT' : 'REPLIED', latencyMs: value })), ...pingChanges },
  }], ...changes } as NetworkDiagnosticRun
}

describe('Quick Ping history trends', () => {
  it('keeps Quick and Long Ping separate and counts only actual probes after pauses', () => {
    const long = run('long', [0, null, 20], {}, { requestedSamples: 60 })
    long.items[0]!.kind = 'LONG_PING'
    const groups = buildQuickPingTrends([run('quick', [100]), long]).groups
    expect(groups).toHaveLength(2)
    expect(groups.find(group => group.kind === 'LONG_PING')).toMatchObject({
      sent: 3, received: 2, lost: 1, unsent: 57, lossPercent: 33.33, averageMs: 10, p95Ms: 20,
    })
    expect(groups.find(group => group.kind === 'QUICK_PING')).toMatchObject({ sent: 1, averageMs: 100 })
    const csv = quickPingTrendCsv(groups, { loadedMs: 3000, failedRuns: [] })
    expect(csv).toContain('diagnostic_kind')
    expect(csv).toContain('"LONG_PING"')
    expect(csv).toContain('"QUICK_PING"')
  })
  it('skips every active Long Ping state and does not turn a finished empty window into loss', () => {
    const active = ['QUEUED', 'RUNNING', 'PAUSING', 'PAUSED'].map(state => {
      const value = run(state, [1]); value.items[0]!.kind = 'LONG_PING'; value.items[0]!.state = state; return value
    })
    const empty = run('empty', [], {}, { requestedSamples: 60 }); empty.items[0]!.kind = 'LONG_PING'
    empty.items[0]!.state = 'NO_EVIDENCE'
    const result = buildQuickPingTrends([...active, empty])
    expect(result.pending).toBe(4)
    expect(result.groups).toHaveLength(1)
    expect(result.groups[0]).toMatchObject({ kind: 'LONG_PING', sent: 0, lost: 0, unsent: 60, lossPercent: null, p95Ms: null })
    expect(result.groups[0]!.points).toHaveLength(1)
  })
  it('retains Long Ping completion boundaries and rejects out-of-window samples', () => {
    const long = run('long', [1], { startedMs: 1000, endedMs: 2000 }, { requestedSamples: 60 })
    long.items[0]!.kind = 'LONG_PING'
    long.items[0]!.quickPing!.samples[0]!.capturedMs = 2001
    expect(buildQuickPingTrends([long]).groups[0]).toMatchObject({ kind: 'LONG_PING', sent: null, lost: null, averageMs: null })
    long.items[0]!.quickPing!.samples[0]!.capturedMs = 2000
    expect(buildQuickPingTrends([long]).groups[0]).toMatchObject({ sent: 1, lost: 0, unsent: 59 })
  })
  it('weights by actual replied samples, preserves zero and excludes unsent from loss', () => {
    const { groups } = buildQuickPingTrends([run('a', [0]), run('b', [30, 30, 30], {}, { requestedSamples: 5 }), run('c', [null, null])])
    expect(groups).toHaveLength(1)
    expect(groups[0]).toMatchObject({ sent: 6, received: 4, lost: 2, unsent: 2, lossPercent: 33.33,
      averageMs: 22.5, minMs: 0, maxMs: 30, p95Ms: 30 })
    expect(groups[0]!.points[0]!.averageMs).toBe(0)
    expect(groups[0]!.points[0]!.p95Ms).toBe(0)
  })
  it('ranks merged replies rather than averaging run percentiles and excludes loss', () => {
    const { groups } = buildQuickPingTrends([run('base', Array.from({ length: 19 }, (_, i) => i + 1)),
      run('outlier', [1000, null], {}, { p95Ms: 12345 })])
    expect(groups[0]!.p95Ms).toBe(19)
    expect(groups[0]!.points.find(point => point.runId === 'outlier')!.p95Ms).toBe(1000)
    expect(buildQuickPingTrends([run('21', Array.from({ length: 21 }, (_, i) => i + 1))]).groups[0]!.p95Ms).toBe(20)
    expect(buildQuickPingTrends([run('loss', [null, null])]).groups[0]!.p95Ms).toBeNull()
  })
  it('keeps exact actor/Site/device/protocol/address/port partitions independent', () => {
    const a = run('a', [1])
    const site = run('s', [2]); site.items[0]!.siteId = 's2'
    const device = run('d', [2]); device.items[0]!.deviceId = 'd2'
    const runs = [a, run('actor', [2], { actor: 'actor-b' }), site, device,
      run('ip', [2], {}, { destinationAddress: '192.0.2.10' }), run('port', [2], {}, { port: 23 }),
      run('icmp', [2], {}, { protocol: 'ICMP', port: null })]
    expect(buildQuickPingTrends(runs).groups).toHaveLength(7)
  })
  it('rejects samples outside run and item time boundaries without reusing an older success', () => {
    for (const boundary of ['created', 'run-start', 'item-start', 'run-end', 'item-end']) {
      const invalid = run(boundary, [999])
      if (boundary === 'created') invalid.items[0]!.quickPing!.samples[0]!.capturedMs = 999
      if (boundary === 'run-start') invalid.startedMs = 1001
      if (boundary === 'item-start') invalid.items[0]!.startedMs = 1001
      if (boundary === 'run-end') {
        invalid.endedMs = 2000; invalid.items[0]!.endedMs = 3000
        invalid.items[0]!.quickPing!.samples[0]!.capturedMs = 2001
      }
      if (boundary === 'item-end') {
        invalid.endedMs = 3000
        invalid.items[0]!.quickPing!.samples[0]!.capturedMs = 2001
      }
      const group = buildQuickPingTrends([run('valid', [0]), invalid]).groups[0]!
      expect(group).toMatchObject({ sent: null, averageMs: null, p95Ms: null })
      expect(group.points.find(point => point.runId === boundary)).toMatchObject({
        metricsKnown: false, sent: null, lossPercent: null, averageMs: null, p95Ms: null, latencies: [],
      })
      expect(group.points.find(point => point.runId === 'valid')!.p95Ms).toBe(0)
    }
  })
  it('accepts samples exactly at valid time boundaries and preserves legacy snapshots without ends', () => {
    const value = run('edge', [0, null], { startedMs: 1000, endedMs: 1001 })
    value.items[0]!.startedMs = 1000; value.items[0]!.endedMs = 1001
    expect(buildQuickPingTrends([value]).groups[0]).toMatchObject({ sent: 2, received: 1, lost: 1, p95Ms: 0 })
    value.endedMs = null; value.items[0]!.endedMs = null
    expect(buildQuickPingTrends([value]).groups[0]!.sent).toBe(2)
    value.startedMs = Number.NaN
    expect(buildQuickPingTrends([value]).groups[0]!.sent).toBeNull()
  })
  it('does not fill missing or inconsistent metrics with zero or an older success', () => {
    const missing = run('missing', []); missing.items[0]!.quickPing = null
    const invalid = run('invalid', [2], {}, { sent: 2 })
    const { groups } = buildQuickPingTrends([run('ok', [0]), missing, invalid])
    expect(groups).toHaveLength(2)
    const knownEndpoint = groups.find(group => group.protocol === 'TCP')!
    expect(knownEndpoint.averageMs).toBeNull()
    expect(knownEndpoint.p95Ms).toBeNull()
    expect(knownEndpoint.sent).toBeNull()
    expect(knownEndpoint.points.find(point => point.runId === 'ok')!.averageMs).toBe(0)
    expect(groups.find(group => group.protocol === 'UNKNOWN')!.points[0]).toMatchObject({ averageMs: null, sent: null })
  })
  it('deduplicates run/item rows, orders time, skips pending and cannot group missing identities', () => {
    const early = run('early', [1]); early.items[0]!.endedMs = 1000
    const pending = run('pending', [2]); pending.items[0]!.state = 'RUNNING'
    const noIdentity = run('unknown', [2], { actor: '' })
    const result = buildQuickPingTrends([run('late', [3]), early, early, pending, noIdentity])
    expect(result).toMatchObject({ pending: 1, missingIdentity: 1 })
    expect(result.groups[0]!.points.map(point => point.runId)).toEqual(['early', 'late'])
    expect(result.groups[0]!.sent).toBe(2)
  })
  it('exports scoped rows, unknowns, failure metadata and neutralizes spreadsheet formulas', () => {
    const value = run('csv', [0], { actor: ' =HYPERLINK("bad")' })
    value.items[0]!.deviceName = '设备,"名称"\n二行'
    const absent = run('absent', []); absent.items[0]!.quickPing = null
    const csv = quickPingTrendCsv(buildQuickPingTrends([value, absent]).groups, { loadedMs: 3000, failedRuns: ['failed'] })
    expect(csv.startsWith('\uFEFF')).toBe(true)
    expect(csv).toContain('"\' =HYPERLINK(""bad"")"')
    expect(csv).toContain('"设备,""名称""\n二行"')
    expect(csv).toContain('"UNKNOWN"')
    expect(csv).toContain('LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS')
    expect(csv).toContain('failed')
    expect(csv).toContain('p95_replied_ms_nearest_rank')
  })
})
