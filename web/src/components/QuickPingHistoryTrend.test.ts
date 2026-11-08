import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, type NetworkDiagnosticRun, type NetworkDiagnosticRunSummary } from '../api/client'
import QuickPingHistoryTrend from './QuickPingHistoryTrend.vue'

const api = vi.hoisted(() => ({ getNetworkDiagnostics: vi.fn() }))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
enableAutoUnmount(afterEach)
const row = (id: string) => ({ id, actor: 'operator', state: 'SUCCEEDED', createdMs: 1000, items: [{
  id: 'item-' + id, kind: 'QUICK_PING', deviceId: 'd1', deviceName: 'Switch', siteId: 's1', state: 'SUCCEEDED',
  quickPing: { protocol: 'TCP', destinationAddress: '192.0.2.1', port: 22, requestedSamples: 1, sent: 1, received: 1, lost: 0,
    samples: [{ sequence: 1, capturedMs: 1000, status: 'REPLIED', latencyMs: 0 }] },
}] } as NetworkDiagnosticRun)
const history = (ids = ['one']) => ids.map(id => ({ id, kinds: ['QUICK_PING'] } as NetworkDiagnosticRunSummary))
const render = (ids = ['one']) => mount(QuickPingHistoryTrend, { props: { history: history(ids), identityKey: 'reader:ADMIN' }, global: { stubs: { 'el-button': { template: '<button><slot/></button>' } } } })
const load = async (wrapper: ReturnType<typeof render>) => { await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises() }

describe('Quick Ping history workspace', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getNetworkDiagnostics.mockImplementation((id: string) => Promise.resolve(row(id))) })
  it('only reads saved runs on explicit load and displays zero accurately', async () => {
    const wrapper = render()
    expect(api.getNetworkDiagnostics).not.toHaveBeenCalled()
    await load(wrapper)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledWith('one')
    expect(wrapper.get('table').text()).toContain('0 ms')
    expect(wrapper.text()).toContain('仅含已发送样本')
    expect(wrapper.text()).toContain('operator · s1 · TCP · 192.0.2.1:22')
    expect(wrapper.text()).toContain('P95 0 ms')
    expect(wrapper.get('table th:last-child').text()).toBe('P95 RTT')
    expect(wrapper.get('table td:last-child').text()).toBe('0 ms')
  })
  it('shows unknown P95 for all-loss history rather than a zero latency', async () => {
    const result = row('one'), ping = result.items[0]!.quickPing!
    ping.received = 0; ping.lost = 1
    ping.samples[0]!.status = 'TIMED_OUT'; ping.samples[0]!.latencyMs = null
    api.getNetworkDiagnostics.mockResolvedValueOnce(result)
    const wrapper = render(); await load(wrapper)
    expect(wrapper.text()).toContain('P95 UNKNOWN ms')
    expect(wrapper.get('table td:last-child').text()).toBe('UNKNOWN ms')
  })
  it('marks partial unreadable history and keeps only successful records', async () => {
    api.getNetworkDiagnostics.mockRejectedValueOnce(new Error('unavailable'))
    const wrapper = render(['failed', 'one']); await load(wrapper)
    expect(wrapper.get('[role=alert]').text()).toContain('failed')
    expect(wrapper.text()).toContain('已读 1 个任务')
    expect(wrapper.find('table').exists()).toBe(true)
  })
  it('drops all results on authorization failure instead of exposing earlier partial reads', async () => {
    api.getNetworkDiagnostics.mockRejectedValueOnce(new ApiRequestError(403, 'FORBIDDEN'))
    const wrapper = render(['failed', 'one']); await load(wrapper)
    expect(wrapper.get('[role=alert]').text()).toContain('当前权限')
    expect(wrapper.find('table').exists()).toBe(false)
  })
  it('discards late results when identity or history changes', async () => {
    let resolve!: (value: NetworkDiagnosticRun) => void
    api.getNetworkDiagnostics.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render()
    await wrapper.findAll('button')[0]!.trigger('click')
    await wrapper.setProps({ identityKey: 'other:ADMIN' })
    resolve(row('one')); await flushPromises()
    expect(wrapper.find('table').exists()).toBe(false)
    await load(wrapper)
    expect(wrapper.find('table').exists()).toBe(true)
    await wrapper.setProps({ history: [] })
    expect(wrapper.find('table').exists()).toBe(false)
  })
  it('bounds reads to 20 runs and four concurrent requests', async () => {
    let active = 0, maximum = 0
    api.getNetworkDiagnostics.mockImplementation(async (id: string) => { ++active; maximum = Math.max(maximum, active); await Promise.resolve(); --active; return row(id) })
    const wrapper = render(Array.from({ length: 25 }, (_, index) => String(index))); await load(wrapper)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(20)
    expect(maximum).toBeLessThanOrEqual(4)
    expect(wrapper.text()).toContain('已达 20 任务上限')
  })
  it('exports only exact selected Site rows and does not perform new API reads', async () => {
    api.getNetworkDiagnostics.mockImplementation(async (id: string) => {
      const result = row(id); result.items[0]!.siteId = id === 'two' ? 's2' : 's1'; return result
    })
    const wrapper = render(['one', 'two']); await load(wrapper)
    await wrapper.get('[aria-label="趋势 Site"]').setValue('s1')
    const parts: BlobPart[][] = []
    vi.stubGlobal('Blob', class { constructor(value: BlobPart[]) { parts.push(value) } })
    vi.stubGlobal('URL', { createObjectURL: vi.fn(() => 'blob:history'), revokeObjectURL: vi.fn() })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    try {
      await wrapper.findAll('button').find(button => button.text() === '导出趋势 CSV')!.trigger('click')
      expect(String(parts[0]![0])).toContain('"one"')
      expect(String(parts[0]![0])).not.toContain('"two"')
      expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2)
    } finally { click.mockRestore(); vi.unstubAllGlobals() }
  })
})
