import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { KeepAlive } from 'vue'
import { ApiRequestError, type NetworkDiagnosticItem } from '../api/client'
import type { PingLossEvidenceSnapshot } from '../api/pingLossEvidence'
import PingLossEvidence from './PingLossEvidence.vue'

const api = vi.hoisted(() => ({ getPingLossEvidence: vi.fn() }))
vi.mock('../api/pingLossEvidence', () => api)
enableAutoUnmount(afterEach)
const item = (): NetworkDiagnosticItem => ({ id: 'loss', deviceId: 'd1', deviceName: 'Switch', siteId: 's1', kind: 'LONG_PING',
  state: 'RUNNING', evidenceCount: 1, evidenceSources: ['LONG_PING_ICMP'],
  quickPing: { protocol: 'ICMP', destinationAddress: '192.0.2.1', port: null, requestedSamples: 60, sent: 3, received: 1, lost: 2,
    lossPercent: 66.67, minMs: 1, maxMs: 1, averageMs: 1, samples: [
      { sequence: 1, capturedMs: 1000, status: 'REPLIED', latencyMs: 1 },
      { sequence: 2, capturedMs: 2000, status: 'TIMED_OUT', latencyMs: null },
      { sequence: 4, capturedMs: 4000, status: 'UNREACHABLE', latencyMs: null },
    ] } })
const response = (): PingLossEvidenceSnapshot => ({ runId: 'one', itemId: 'loss', deviceId: 'd1', siteId: 's1', sequence: 2,
  sampleCapturedMs: 2000, sampleStatus: 'TIMED_OUT', destinationAddress: '192.0.2.1', protocol: 'ICMP', port: null,
  state: 'HISTORICAL_CONTEXT', connectedApState: 'UNKNOWN', connectedApReason: 'NO_AUTHORITATIVE_CLIENT_AP_ASSOCIATION',
  facts: [{ id: 'evidence-1', kind: 'interface', identityKey: 'GE1/0/1', status: 'STALE', source: 'H3C_TASK_RAW:raw-run',
    capturedScopeKey: 'exact-scope', capturedMs: 1500, collectionEndedMs: 1600, ageMs: 500, observedValues: { linkStatus: 'UP' },
    rawReferences: [{ runId: 'raw-run', command: 'display interface brief', outputSha256: 'output-hash', segmentSha256: 'segment-hash' }] }],
  hasMore: false, limitations: [] })
const render = () => mount(PingLossEvidence, { props: { runId: 'one', item: item(), identityKey: 'reader:ADMIN' } })
async function load(wrapper: ReturnType<typeof render>) {
  await wrapper.get('select').setValue('2'); await wrapper.get('button').trigger('click'); await flushPromises()
}

describe('Long Ping loss evidence', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getPingLossEvidence.mockImplementation(async () => response()) })
  it('reads only after selecting a real lost probe; unsent pause gaps are not selectable', async () => {
    const wrapper = render()
    expect(api.getPingLossEvidence).not.toHaveBeenCalled()
    expect(wrapper.get('button').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('option').map(option => option.attributes('value'))).toEqual(['', '2', '4'])
    await load(wrapper)
    expect(api.getPingLossEvidence).toHaveBeenCalledTimes(1)
    expect(api.getPingLossEvidence).toHaveBeenCalledWith('one', 'loss', 2)
    expect(wrapper.text()).toContain('接入 AP：未知（UNKNOWN）')
    expect(wrapper.text()).toContain('距采样 500 ms')
    expect(wrapper.text()).toContain('linkStatus: UP')
    expect(wrapper.text()).toContain('output-hash')
    expect(wrapper.text()).toContain('证据发布时间未保存')
  })
  it('hides evidence when there are no actual loss samples', async () => {
    const wrapper = render(), next = item()
    next.quickPing!.samples = next.quickPing!.samples.filter(sample => sample.status === 'REPLIED')
    await wrapper.setProps({ item: next })
    expect(wrapper.find('section').exists()).toBe(false)
    expect(api.getPingLossEvidence).not.toHaveBeenCalled()
  })
  it('reports target binding and empty evidence explicitly', async () => {
    api.getPingLossEvidence.mockResolvedValueOnce({ ...response(), state: 'TARGET_BINDING_UNPROVEN', facts: [] })
    const wrapper = render(); await load(wrapper)
    expect(wrapper.text()).toContain('目标登记已变化')
    expect(wrapper.text()).toContain('接入 AP：未知')
    api.getPingLossEvidence.mockResolvedValueOnce({ ...response(), state: 'NO_PRIOR_VERIFIED_EVIDENCE', facts: [] })
    await load(wrapper)
    expect(wrapper.text()).toContain('采样前没有可验证')
  })
  it('discloses a bounded evidence list', async () => {
    api.getPingLossEvidence.mockResolvedValueOnce({ ...response(), hasMore: true })
    const wrapper = render(); await load(wrapper)
    expect(wrapper.text()).toContain('仅展示最近 20 个证据身份')
  })
  it.each(['runId', 'itemId', 'deviceId', 'siteId', 'destinationAddress', 'protocol'] as const)('rejects mismatched %s', async field => {
    api.getPingLossEvidence.mockResolvedValueOnce({ ...response(), [field]: 'other' })
    const wrapper = render(); await load(wrapper)
    expect(wrapper.find('[role=alert]').exists()).toBe(true)
    expect(wrapper.find('.loss-result').exists()).toBe(false)
  })
  it.each(['capture', 'completion', 'age', 'association'] as const)('rejects invalid temporal or association boundary %s', async boundary => {
    const next = response()
    if (boundary === 'capture') next.facts[0]!.capturedMs = 2001
    if (boundary === 'completion') next.facts[0]!.collectionEndedMs = 2001
    if (boundary === 'age') next.facts[0]!.ageMs = -1
    if (boundary === 'association') Object.assign(next, { connectedApState: 'CONFIRMED' })
    api.getPingLossEvidence.mockResolvedValueOnce(next)
    const wrapper = render(); await load(wrapper)
    expect(wrapper.find('.loss-result').exists()).toBe(false)
    expect(wrapper.get('[role=alert]').text()).toContain('采样范围不符')
  })
  it('clears previously displayed data on authorization failure', async () => {
    const wrapper = render(); await load(wrapper)
    api.getPingLossEvidence.mockRejectedValueOnce(new ApiRequestError(403, 'FORBIDDEN'))
    await load(wrapper)
    expect(wrapper.find('.loss-result').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('evidence-1')
  })
  it.each(['identity', 'run', 'target', 'sample'] as const)('discards late responses after %s changes', async change => {
    let resolve!: (value: PingLossEvidenceSnapshot) => void
    api.getPingLossEvidence.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render(); await load(wrapper)
    if (change === 'identity') await wrapper.setProps({ identityKey: 'other:ADMIN' })
    if (change === 'run') await wrapper.setProps({ runId: 'two' })
    if (change === 'target') await wrapper.setProps({ item: { ...item(), deviceId: 'other' } })
    if (change === 'sample') await wrapper.get('select').setValue('4')
    resolve(response()); await flushPromises()
    expect(wrapper.find('.loss-result').exists()).toBe(false)
  })
  it('keeps a selected historical result while polling appends newer samples', async () => {
    const wrapper = render(); await load(wrapper)
    const next = item(); next.quickPing!.samples.push({ sequence: 5, capturedMs: 5000, status: 'TIMED_OUT', latencyMs: null })
    await wrapper.setProps({ item: next })
    expect(wrapper.find('.loss-result').exists()).toBe(true)
    next.quickPing!.samples[1]!.capturedMs = 2100
    await wrapper.setProps({ item: { ...next } })
    expect(wrapper.find('.loss-result').exists()).toBe(false)
  })
  it('invalidates an in-flight response when deactivated from KeepAlive', async () => {
    let resolve!: (value: PingLossEvidenceSnapshot) => void
    api.getPingLossEvidence.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = mount({ components: { KeepAlive, PingLossEvidence }, props: ['visible'],
      setup: () => ({ target: item() }),
      template: '<KeepAlive><PingLossEvidence v-if="visible" run-id="one" :item="target" identity-key="reader:ADMIN" /></KeepAlive>',
    }, { props: { visible: true } })
    await wrapper.get('select').setValue('2'); await wrapper.get('button').trigger('click')
    await wrapper.setProps({ visible: false }); resolve(response()); await flushPromises()
    await wrapper.setProps({ visible: true })
    expect(wrapper.find('.loss-result').exists()).toBe(false)
  })
})
