import type { NetworkDiagnosticRun, QuickPingSnapshot } from './client'

export interface QuickPingTrendPoint {
  runId: string; itemId: string; capturedMs: number; state: string; requested: number | null
  sent: number | null; received: number | null; lost: number | null; unsent: number | null
  lossPercent: number | null; averageMs: number | null; p95Ms: number | null; latencies: number[]; metricsKnown: boolean
}
export interface QuickPingTrendGroup {
  key: string; actor: string; siteId: string; deviceId: string; deviceName: string
  protocol: string; endpoint: string; port: number | null; points: QuickPingTrendPoint[]
  sent: number | null; received: number | null; lost: number | null; unsent: number | null
  lossPercent: number | null; averageMs: number | null; minMs: number | null; maxMs: number | null; p95Ms: number | null
}
const count = (n: unknown): n is number => Number.isInteger(n) && Number(n) >= 0
const finiteLatency = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n) && n >= 0
const round = (n: number) => Math.round(n * 100) / 100
// Nearest-rank percentile of actual replies, never an average of per-run percentiles.
const p95 = (latencies: readonly number[]) => latencies.length
  ? [...latencies].sort((a, b) => a - b)[Math.ceil(latencies.length * .95) - 1]! : null
function point(run: NetworkDiagnosticRun, item: NetworkDiagnosticRun['items'][number], ping: QuickPingSnapshot | null | undefined): QuickPingTrendPoint {
  const sent = ping?.sent, received = ping?.received, lost = ping?.lost, requested = ping?.requestedSamples
  const samples = Array.isArray(ping?.samples) ? ping.samples : []
  const replied = samples.filter(sample => sample.status === 'REPLIED' && finiteLatency(sample.latencyMs))
  const known = count(sent) && count(received) && count(lost) && count(requested) && sent <= requested &&
    received + lost === sent && samples.length === sent && replied.length === received &&
    samples.every(sample => Number.isInteger(sample.sequence) && sample.sequence > 0 && sample.sequence <= requested &&
      Number.isFinite(sample.capturedMs) && sample.capturedMs > 0 &&
      (sample.status === 'REPLIED' ? finiteLatency(sample.latencyMs) :
        ['TIMED_OUT', 'UNREACHABLE'].includes(sample.status) && sample.latencyMs == null)) &&
    new Set(samples.map(sample => sample.sequence)).size === sent
  const latencies = known ? replied.map(sample => sample.latencyMs!) : []
  return { runId: run.id, itemId: item.id,
    capturedMs: item.endedMs ?? item.startedMs ?? run.endedMs ?? run.createdMs, state: item.state,
    requested: count(requested) ? requested : null,
    sent: known ? sent! : null, received: known ? received! : null, lost: known ? lost! : null,
    unsent: known ? requested! - sent! : null, lossPercent: known && sent ? round(lost! * 100 / sent) : null,
    averageMs: latencies.length ? round(latencies.reduce((a, b) => a + b, 0) / latencies.length) : null,
    p95Ms: p95(latencies),
    latencies, metricsKnown: known }
}

export function buildQuickPingTrends(runs: readonly NetworkDiagnosticRun[]): { groups: QuickPingTrendGroup[]; pending: number; missingIdentity: number } {
  const groups = new Map<string, QuickPingTrendGroup>()
  const seen = new Set<string>()
  let pending = 0, missingIdentity = 0
  for (const run of runs) for (const item of run.items) {
    if (item.kind !== 'QUICK_PING') continue
    const identity = JSON.stringify([run.id, item.id])
    if (seen.has(identity)) continue
    seen.add(identity)
    if (item.state === 'RUNNING' || item.state === 'QUEUED') { ++pending; continue }
    if (!run.actor || !item.siteId || !item.deviceId) { ++missingIdentity; continue }
    const ping = item.quickPing
    const hasEndpoint = ping && ['ICMP', 'TCP'].includes(ping.protocol) && !!ping.destinationAddress &&
      (ping.protocol === 'ICMP' ? ping.port === null : Number.isInteger(ping.port) && ping.port! > 0 && ping.port! <= 65535)
    const protocol = hasEndpoint ? ping.protocol : 'UNKNOWN'
    const endpoint = hasEndpoint ? ping.destinationAddress : 'UNKNOWN'
    const port = hasEndpoint ? ping.port : null
    // Missing endpoint snapshots cannot establish comparability with another run.
    const key = JSON.stringify([run.actor, item.siteId, item.deviceId, protocol, endpoint, port,
      ...(hasEndpoint ? [] : [run.id, item.id])])
    let group = groups.get(key)
    if (!group) {
      group = { key, actor: run.actor, siteId: item.siteId, deviceId: item.deviceId, deviceName: item.deviceName,
        protocol, endpoint, port, points: [], sent: null, received: null, lost: null, unsent: null,
        lossPercent: null, averageMs: null, minMs: null, maxMs: null, p95Ms: null }
      groups.set(key, group)
    }
    group.points.push(point(run, item, hasEndpoint ? ping : null))
  }
  for (const group of groups.values()) {
    group.points.sort((a, b) => a.capturedMs - b.capturedMs || a.runId.localeCompare(b.runId) || a.itemId.localeCompare(b.itemId))
    if (!group.points.every(point => point.metricsKnown)) continue
    group.sent = group.points.reduce((sum, point) => sum + point.sent!, 0)
    group.received = group.points.reduce((sum, point) => sum + point.received!, 0)
    group.lost = group.points.reduce((sum, point) => sum + point.lost!, 0)
    group.unsent = group.points.reduce((sum, point) => sum + point.unsent!, 0)
    group.lossPercent = group.sent ? round(100 * group.lost / group.sent) : null
    const latencies = group.points.flatMap(point => point.latencies)
    group.averageMs = latencies.length ? round(latencies.reduce((a, b) => a + b, 0) / latencies.length) : null
    group.minMs = latencies.length ? Math.min(...latencies) : null
    group.maxMs = latencies.length ? Math.max(...latencies) : null
    group.p95Ms = p95(latencies)
  }
  return { groups: [...groups.values()].sort((a, b) => a.key.localeCompare(b.key)), pending, missingIdentity }
}

export function quickPingTrendCsv(groups: readonly QuickPingTrendGroup[], context: { loadedMs: number; failedRuns: readonly string[] }): string {
  const header = ['actor', 'site_id', 'device_id', 'device_name', 'protocol', 'destination_address', 'port',
    'run_id', 'item_id', 'captured_utc', 'state', 'requested', 'sent', 'received', 'lost', 'unsent_unknown',
    'loss_percent_sent_only', 'average_replied_ms', 'p95_replied_ms_nearest_rank', 'metrics_known', 'loaded_utc', 'failed_run_ids', 'export_scope']
  const rows: Array<Array<string | number | boolean | null>> = [header]
  for (const group of groups) for (const point of group.points) rows.push([
    group.actor, group.siteId, group.deviceId, group.deviceName, group.protocol, group.endpoint, group.port,
    point.runId, point.itemId, new Date(point.capturedMs).toISOString(), point.state, point.requested,
    point.sent, point.received, point.lost, point.unsent, point.lossPercent, point.averageMs, point.p95Ms, point.metricsKnown,
    new Date(context.loadedMs).toISOString(), JSON.stringify(context.failedRuns), 'LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS',
  ])
  const cell = (value: string | number | boolean | null) => {
    const text = value === null ? 'UNKNOWN' : String(value)
    const safe = /^[\s\u0000-\u001f]*[=+\-@]/.test(text) || /^[\t\r\n]/.test(text) ? "'" + text : text
    return '"' + safe.replaceAll('"', '""') + '"'
  }
  return '\uFEFF' + rows.map(row => row.map(cell).join(',')).join('\r\n') + '\r\n'
}
