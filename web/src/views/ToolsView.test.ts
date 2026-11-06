import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ElMessage } from 'element-plus'
import type { NetworkDiagnosticItem, NetworkDiagnosticRun } from '../api/client'
import { useAuthStore } from '../stores/auth'
import ToolsView from './ToolsView.vue'

const api = vi.hoisted(() => ({ getHostTools: vi.fn(), getTerminalDevices: vi.fn(), getDeviceInventory: vi.fn(),
  updateHostTool: vi.fn(), launchHostTool: vi.fn(), startNetworkDiagnostics: vi.fn(), getNetworkDiagnostics: vi.fn(), cancelNetworkDiagnostics: vi.fn() }))
vi.mock('../api/client', () => api)
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

function item(overrides: Partial<NetworkDiagnosticItem> = {}): NetworkDiagnosticItem {
  return { id: 'trace-one', deviceId: 'device-one', deviceName: '交换机甲', siteId: 'site-one', kind: 'TRACEROUTE',
    state: 'SUCCEEDED', outcome: 'OK', detail: null, latencyMs: 12, evidenceCount: 0, evidenceSources: [],
    traceRoute: { destinationAddress: '192.0.2.10', destinationReached: true, maxHops: 30, hops: [
      { ttl: 1, address: '192.0.2.1', status: 'TTL_EXPIRED', latencyMs: 2 },
      { ttl: 2, address: null, status: 'TIMED_OUT', latencyMs: null },
      { ttl: 3, address: '192.0.2.10', status: 'REACHED', latencyMs: 3 },
    ] }, ...overrides }
}
function run(overrides: Partial<NetworkDiagnosticRun> = {}): NetworkDiagnosticRun {
  return { id: 'diagnostic-one', actor: 'tester', state: 'SUCCEEDED', timeoutMs: 30000, totalDevices: 1, totalChecks: 1,
    completedChecks: 1, succeededChecks: 1, failedChecks: 0, noEvidenceChecks: 0, cancelledChecks: 0, queuedChecks: 0,
    runningChecks: 0, progressPercent: 100, createdMs: 1000, items: [item()], ...overrides }
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve } }
const wrappers: VueWrapper[] = []
async function render(role = 'SUPERADMIN') {
  const pinia = createPinia(); setActivePinia(pinia); useAuthStore().user = { username: 'tester', role } as never
  const wrapper = mount(ToolsView, { global: { plugins: [pinia], directives: { loading: {} }, stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['loading'], template: '<button :data-loading="loading"><slot/></button>' },
  } } })
  wrappers.push(wrapper); await flushPromises(); return wrapper
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(value => value.text() === text)! }
async function selectTrace(wrapper: VueWrapper, ids = ['device-one']) {
  await wrapper.get('select[aria-label="诊断设备"]').setValue(ids)
  for (const kind of ['PING', 'TCP', 'DNS']) await wrapper.get(`input[value="${kind}"]`).setValue(false)
  await wrapper.get('input[value="TRACEROUTE"]').setValue(true)
}

describe('network diagnostic tools and traceroute', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getHostTools.mockResolvedValue([]); api.getTerminalDevices.mockResolvedValue([])
    api.getDeviceInventory.mockResolvedValue({ items: [
      { id: 'device-one', name: '交换机甲', managementIp: '192.0.2.10', enabled: true, isValid: true },
      { id: 'device-two', name: '路由器乙', managementIp: '192.0.2.20', enabled: true, isValid: true },
      { id: 'invalid-device', enabled: true, isValid: false },
    ] })
    api.startNetworkDiagnostics.mockResolvedValue(run()); api.getNetworkDiagnostics.mockResolvedValue(run())
    api.cancelNetworkDiagnostics.mockResolvedValue({ cancelled: true })
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

  it('opens without running probes and offers optional traceroute only for valid inventory devices', async () => {
    const wrapper = await render()
    expect((wrapper.get('input[value="TRACEROUTE"]').element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.get('select[aria-label="诊断设备"]').findAll('option').map(option => option.attributes('value')))
      .toEqual(['device-one', 'device-two'])
    expect(wrapper.text()).toContain('从 NetConsole 服务所在主机')
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled(); expect(api.getNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('submits exact batch device UUIDs and traceroute with a 30 second total deadline', async () => {
    const wrapper = await render(); await selectTrace(wrapper, ['device-one', 'device-two'])
    expect((wrapper.get('select[aria-label="每项检查总超时"]').element as HTMLSelectElement).value).toBe('30000')
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(api.startNetworkDiagnostics).toHaveBeenCalledWith({ deviceIds: ['device-one', 'device-two'], kinds: ['TRACEROUTE'], timeoutMs: 30000 })
    expect(api.getNetworkDiagnostics).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('已完成 1 / 1 项检查')
    expect(wrapper.get('progress').attributes('value')).toBe('100')
  })

  it('allows an explicit shorter deadline after selecting traceroute and preserves other diagnostic kinds', async () => {
    const wrapper = await render(); await wrapper.get('select[aria-label="诊断设备"]').setValue(['device-one'])
    await wrapper.get('input[value="TRACEROUTE"]').setValue(true)
    await wrapper.get('select[aria-label="每项检查总超时"]').setValue('5000')
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(api.startNetworkDiagnostics).toHaveBeenCalledWith({ deviceIds: ['device-one'], kinds: ['PING', 'TCP', 'DNS', 'TRACEROUTE'], timeoutMs: 5000 })
  })

  it('renders hop addresses, latency and Chinese statuses without treating a timed out hop as the destination', async () => {
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    const route = wrapper.get('section[aria-label="交换机甲 Traceroute 路径"]')
    expect(route.text()).toContain('目标 192.0.2.10'); expect(route.text()).toContain('已记录 3 / 30 跳')
    expect(route.findAll('tbody tr').map(row => row.findAll('td').map(cell => cell.text()))).toEqual([
      ['1', '192.0.2.1', '中间节点', '2 ms'], ['2', '—', '该跳超时', '—'], ['3', '192.0.2.10', '已到达目标', '3 ms'],
    ])
    expect(route.text()).toContain('超时节点可能不响应探测')
  })

  it('keeps a cancelled partial route and shows unreachable and other responses truthfully', async () => {
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'CANCELLED', cancelledChecks: 1, succeededChecks: 0, items: [item({
      state: 'CANCELLED', outcome: 'CANCELLED', traceRoute: { destinationAddress: '192.0.2.10', destinationReached: false, maxHops: 30,
        hops: [{ ttl: 1, address: '192.0.2.1', status: 'UNREACHABLE', latencyMs: 2 }, { ttl: 2, address: null, status: 'OTHER', latencyMs: null }] },
    })] }))
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('已取消'); expect(wrapper.text()).toContain('未到达目标'); expect(wrapper.text()).toContain('不可达')
    expect(wrapper.text()).toContain('其他响应'); expect(wrapper.text()).not.toContain('已到达目标')
    expect(button(wrapper, '取消任务').attributes('disabled')).toBeDefined()
  })

  it('polls progress and cancels an active batch, ignoring an older running response', async () => {
    const late = deferred<NetworkDiagnosticRun>()
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'RUNNING', completedChecks: 0, progressPercent: 0 }))
    api.getNetworkDiagnostics.mockReturnValueOnce(late.promise).mockResolvedValueOnce(run({ state: 'CANCELLED' }))
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(button(wrapper, '开始诊断').attributes('disabled')).toBeDefined()
    await button(wrapper, '开始诊断').trigger('click'); expect(api.startNetworkDiagnostics).toHaveBeenCalledTimes(1)
    await button(wrapper, '取消任务').trigger('click'); await flushPromises()
    expect(api.cancelNetworkDiagnostics).toHaveBeenCalledWith('diagnostic-one'); expect(wrapper.text()).toContain('已取消 · 100%')
    late.resolve(run({ state: 'RUNNING', progressPercent: 0 })); await flushPromises()
    expect(wrapper.text()).toContain('已取消 · 100%'); expect(wrapper.text()).not.toContain('执行中 · 0%')
  })

  it('updates intermediate hops automatically and stops polling when the batch completes', async () => {
    vi.useFakeTimers()
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'QUEUED', progressPercent: 0 }))
    api.getNetworkDiagnostics.mockResolvedValueOnce(run({ state: 'RUNNING', progressPercent: 0, completedChecks: 0, items: [item({
      state: 'RUNNING', traceRoute: { destinationAddress: '192.0.2.10', destinationReached: false, maxHops: 30,
        hops: [{ ttl: 1, address: '192.0.2.1', status: 'TTL_EXPIRED', latencyMs: 1 }] },
    })] })).mockResolvedValueOnce(run())
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('正在探测路径'); expect(wrapper.text()).toContain('已记录 1 / 30 跳')
    await vi.advanceTimersByTimeAsync(700); await flushPromises()
    expect(wrapper.text()).toContain('已完成 · 100%')
    await vi.advanceTimersByTimeAsync(1400); expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2)
  })

  it('keeps progress read errors visible and allows retry without launching another batch', async () => {
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'RUNNING', progressPercent: 0 }))
    api.getNetworkDiagnostics.mockRejectedValueOnce(new Error('诊断进度读取失败')).mockResolvedValueOnce(run())
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('诊断进度读取失败')
    await button(wrapper, '重新读取进度').trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false); expect(api.startNetworkDiagnostics).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('已完成 · 100%')
  })

  it('reports a rejected start and prevents duplicate submissions while it is pending', async () => {
    const pending = deferred<NetworkDiagnosticRun>(); api.startNetworkDiagnostics.mockReturnValue(pending.promise)
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click')
    await button(wrapper, '开始诊断').trigger('click'); expect(api.startNetworkDiagnostics).toHaveBeenCalledTimes(1)
    pending.resolve(run({ state: 'FAILED', failureReason: '诊断执行失败', items: [item({ traceRoute: null, state: 'FAILED', outcome: 'TIMEOUT' })] })); await flushPromises()
    expect(wrapper.text()).toContain('诊断执行失败'); expect(wrapper.text()).toContain('检查总超时'); expect(wrapper.text()).toContain('暂无逐跳响应')
    api.startNetworkDiagnostics.mockRejectedValue(new Error('诊断任务启动失败'))
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('诊断任务启动失败')
    expect(button(wrapper, '开始诊断').attributes('disabled')).toBeUndefined()
  })

  it('requires devices and kinds and keeps lower roles from starting or cancelling', async () => {
    const wrapper = await render(); await button(wrapper, '开始诊断').trigger('click')
    expect(ElMessage.warning).toHaveBeenCalledWith('请选择至少一台设备')
    await wrapper.get('select[aria-label="诊断设备"]').setValue(['device-one'])
    for (const kind of ['PING', 'TCP', 'DNS']) await wrapper.get(`input[value="${kind}"]`).setValue(false)
    await button(wrapper, '开始诊断').trigger('click'); expect(ElMessage.warning).toHaveBeenCalledWith('请选择至少一种诊断类型')
    const reader = await render('ADMIN'); await selectTrace(reader)
    expect(button(reader, '开始诊断').attributes('disabled')).toBeDefined()
    await button(reader, '开始诊断').trigger('click'); await button(reader, '取消任务').trigger('click')
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled(); expect(api.cancelNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('accepts historical results without route metadata and stops updates after unmount or identity changes', async () => {
    vi.useFakeTimers()
    api.startNetworkDiagnostics.mockResolvedValue(run({ items: [item({ traceRoute: undefined })] }))
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('暂无逐跳响应')
    const late = deferred<NetworkDiagnosticRun>()
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'RUNNING' })); api.getNetworkDiagnostics.mockReturnValue(late.promise)
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    useAuthStore().user = { username: 'other-user', role: 'ADMIN' } as never; await flushPromises()
    expect(wrapper.text()).toContain('尚未运行诊断')
    wrapper.unmount(); late.resolve(run()); await flushPromises(); await vi.advanceTimersByTimeAsync(1400)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(1)
  })
})
