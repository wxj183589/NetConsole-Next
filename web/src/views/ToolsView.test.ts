import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ElMessage } from 'element-plus'
import { ApiRequestError, type NetworkDiagnosticItem, type NetworkDiagnosticRun, type NetworkDiagnosticRunSummary } from '../api/client'
import { useAuthStore } from '../stores/auth'
import ToolsView from './ToolsView.vue'

const api = vi.hoisted(() => ({ getHostTools: vi.fn(), getTerminalDevices: vi.fn(), getDeviceInventory: vi.fn(),
  updateHostTool: vi.fn(), launchHostTool: vi.fn(), startNetworkDiagnostics: vi.fn(), listNetworkDiagnostics: vi.fn(), getNetworkDiagnostics: vi.fn(), cancelNetworkDiagnostics: vi.fn(), setLongPingPaused: vi.fn() }))
vi.mock('../api/client', async importOriginal => ({ ...await importOriginal<typeof import('../api/client')>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

function item(overrides: Partial<NetworkDiagnosticItem> = {}): NetworkDiagnosticItem {
  return { id: 'trace-one', deviceId: 'device-one', deviceName: '交换机甲', siteId: 'site-one', kind: 'TRACEROUTE',
    state: 'SUCCEEDED', outcome: 'OK', detail: null, latencyMs: 12, evidenceCount: 0, evidenceSources: [],
    traceRoute: { destinationAddress: '192.0.2.10', destinationReached: true, maxHops: 30, probeKind: 'WINDOWS_SYSTEM_ICMP_TRACERT', hops: [
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
function summary(value = run()): NetworkDiagnosticRunSummary {
  return { id: value.id, state: value.state, totalDevices: value.totalDevices, totalChecks: value.totalChecks,
    completedChecks: value.completedChecks, progressPercent: value.progressPercent, createdMs: value.createdMs,
    kinds: [...new Set(value.items.map(item => item.kind))] }
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve } }
const wrappers: VueWrapper[] = []
async function render(role = 'SUPERADMIN') {
  const pinia = createPinia(); setActivePinia(pinia); useAuthStore().user = { username: 'tester', role } as never
  const wrapper = mount(ToolsView, { global: { plugins: [pinia], directives: { loading: {} }, stubs: {
    SyslogWorkspace: true,
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
    api.listNetworkDiagnostics.mockResolvedValue([])
    api.cancelNetworkDiagnostics.mockResolvedValue({ cancelled: true })
    api.setLongPingPaused.mockResolvedValue({ accepted: true })
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

  it('shows actual DNS addresses and distinguishes literal IP evidence when reading saved history', async () => {
    const saved = run({ items: [item({ kind: 'DNS', traceRoute: null, dns: {
      query: 'switch.example.test', resolutionMode: 'SYSTEM_RESOLVER',
      addresses: ['192.0.2.40', '2001:db8::40'], capturedMs: 1000,
    } })] })
    api.listNetworkDiagnostics.mockResolvedValue([summary(saved)])
    api.getNetworkDiagnostics.mockResolvedValue(saved)
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断历史"]').setValue(saved.id); await flushPromises()
    const result = wrapper.get('section[aria-label="交换机甲 DNS 结果"]')
    expect(result.text()).toContain('switch.example.test'); expect(result.text()).toContain('系统 DNS 解析')
    expect(result.findAll('li').map(row => row.text())).toEqual(['192.0.2.40', '2001:db8::40'])
    expect(result.text()).not.toContain('未查询 DNS')
    api.getNetworkDiagnostics.mockResolvedValue(run({ id: 'literal', items: [item({ kind: 'DNS', traceRoute: null,
      dns: { query: '192.0.2.40', resolutionMode: 'IP_LITERAL', addresses: ['192.0.2.40'], capturedMs: 2000 } })] }))
    api.listNetworkDiagnostics.mockResolvedValue([summary(saved), summary(run({ id: 'literal' }))])
    await button(wrapper, '刷新历史').trigger('click'); await flushPromises()
    await wrapper.get('select[aria-label="诊断历史"]').setValue('literal'); await flushPromises()
    const literal = wrapper.get('section[aria-label="交换机甲 DNS 结果"]')
    expect(literal.text()).toContain('直接 IP 地址，未查询 DNS')
    expect(literal.text()).not.toContain('系统 DNS 解析')
  })

  it('does not invent DNS addresses for old diagnostic records', async () => {
    const old = run({ items: [item({ kind: 'DNS', traceRoute: null, dns: null, detail: '旧版摘要' })] })
    api.listNetworkDiagnostics.mockResolvedValue([summary(old)])
    api.getNetworkDiagnostics.mockResolvedValue(old)
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断历史"]').setValue(old.id); await flushPromises()
    const result = wrapper.get('section[aria-label="交换机甲 DNS 结果"]')
    expect(result.text()).toContain('没有结构化解析地址')
    expect(result.findAll('li')).toHaveLength(0)
    expect(result.text()).not.toContain('系统 DNS 解析')
  })

  it('pauses and resumes Long Ping while keeping cancellation available and showing window semantics', async () => {
    const active = run({ state: 'RUNNING', completedChecks: 0, progressPercent: 0, items: [item({ kind: 'LONG_PING', state: 'RUNNING', traceRoute: null })] })
    api.startNetworkDiagnostics.mockResolvedValue(active)
    api.getNetworkDiagnostics.mockResolvedValue(active)
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    api.getNetworkDiagnostics.mockResolvedValue({ ...active, pausedChecks: 1, items: [item({ kind: 'LONG_PING', state: 'PAUSED', traceRoute: null })] })
    await button(wrapper, '暂停长期 Ping').trigger('click'); await flushPromises()
    expect(api.setLongPingPaused).toHaveBeenLastCalledWith('diagnostic-one', true)
    expect(wrapper.text()).toContain('已暂停'); expect(wrapper.text()).toContain('原持续时长继续计时')
    expect(button(wrapper, '取消任务').attributes('disabled')).toBeUndefined()
    api.getNetworkDiagnostics.mockResolvedValue(active)
    await button(wrapper, '恢复长期 Ping').trigger('click'); await flushPromises()
    expect(api.setLongPingPaused).toHaveBeenLastCalledWith('diagnostic-one', false)
    api.getNetworkDiagnostics.mockResolvedValue(run({ state: 'CANCELLED' }))
    await button(wrapper, '取消任务').trigger('click'); await flushPromises()
    expect(api.cancelNetworkDiagnostics).toHaveBeenCalledWith('diagnostic-one')
    expect(wrapper.text()).not.toContain('暂停长期 Ping')
  })

  it('ignores a late pause acknowledgement after switching history and resets the control button', async () => {
    const active = run({ state: 'RUNNING', items: [item({ kind: 'LONG_PING', state: 'RUNNING' })] })
    api.listNetworkDiagnostics.mockResolvedValue([summary(active), summary(run({ id: 'other' }))])
    api.getNetworkDiagnostics.mockResolvedValue(active)
    const late = deferred<{ accepted: boolean }>()
    api.setLongPingPaused.mockReturnValue(late.promise)
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    await button(wrapper, '暂停长期 Ping').trigger('click'); await flushPromises()
    api.getNetworkDiagnostics.mockResolvedValue(run({ id: 'other' }))
    await wrapper.get('select[aria-label="诊断历史"]').setValue('other'); await flushPromises()
    late.resolve({ accepted: true }); await flushPromises()
    expect(wrapper.get('select[aria-label="诊断历史"]').element).toHaveProperty('value', 'other')
    expect(wrapper.text()).not.toContain('恢复长期 Ping')
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2)
  })

  it('submits continuous TCP sampling and renders real loss and unavailable latency', async () => {
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'FAILED', items: [item({ kind: 'QUICK_PING', state: 'FAILED', traceRoute: null,
      quickPing: { protocol: 'TCP', destinationAddress: '192.0.2.10', port: 22, requestedSamples: 3, sent: 3, received: 0, lost: 3,
        lossPercent: 100, minMs: null, maxMs: null, averageMs: null, samples: [
          { sequence: 1, capturedMs: 1000, status: 'TIMED_OUT', latencyMs: null },
          { sequence: 2, capturedMs: 1100, status: 'ERROR', latencyMs: null },
          { sequence: 3, capturedMs: 1200, status: 'TIMED_OUT', latencyMs: null },
        ] } })] }))
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断设备"]').setValue(['device-one'])
    for (const kind of ['PING', 'TCP', 'DNS']) await wrapper.get(`input[value="${kind}"]`).setValue(false)
    await wrapper.get('input[value="QUICK_PING"]').setValue(true)
    await wrapper.get('select[aria-label="Quick Ping 协议"]').setValue('TCP')
    await wrapper.get('input[aria-label="Quick Ping 采样次数"]').setValue(3)
    await wrapper.get('input[aria-label="Quick Ping 采样间隔"]').setValue(100)
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(api.startNetworkDiagnostics).toHaveBeenCalledWith({ deviceIds: ['device-one'], kinds: ['QUICK_PING'], timeoutMs: 2000, sampleCount: 3, intervalMs: 100, quickPingProtocol: 'TCP' })
    const result = wrapper.get('section[aria-label="交换机甲 Quick Ping 结果"]')
    expect(result.text()).toContain('丢失率 100%'); expect(result.text()).toContain('— / — / — ms')
    expect(result.text()).toContain('P95 UNKNOWN ms')
    expect(result.findAll('.quick-ping-sample')).toHaveLength(3)
  })

  it('uses a compatible timeout with traceroute and rejects over-budget Quick Ping sampling', async () => {
    const wrapper = await render(); await selectTrace(wrapper)
    await wrapper.get('input[value="QUICK_PING"]').setValue(true)
    expect((wrapper.get('select[aria-label="每项检查总超时"]').element as HTMLSelectElement).value).toBe('2000')
    await wrapper.get('input[aria-label="Quick Ping 采样次数"]').setValue(60)
    await wrapper.get('input[aria-label="Quick Ping 采样间隔"]').setValue(5000)
    await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled()
    expect(ElMessage.warning).toHaveBeenCalledWith(expect.stringContaining('总预算不超过 120 秒'))
  })

  it('starts Long Ping with its independent duration while retaining the Quick Ping budget', async () => {
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断设备"]').setValue(['device-one'])
    await wrapper.get('input[value="LONG_PING"]').setValue(true)
    await wrapper.get('select[aria-label="长期 Ping 持续时长"]').setValue('300')
    await wrapper.get('input[aria-label="长期 Ping 采样间隔"]').setValue(1000)
    await button(wrapper, '开始诊断').trigger('click')
    await flushPromises()
    expect(api.startNetworkDiagnostics).toHaveBeenCalledWith(expect.objectContaining({
      kinds: expect.arrayContaining(['LONG_PING']), longPingDurationSeconds: 300, intervalMs: 1000,
    }))
  })

  it('opens without running probes and offers optional traceroute only for valid inventory devices', async () => {
    const wrapper = await render()
    expect((wrapper.get('input[value="TRACEROUTE"]').element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.get('select[aria-label="诊断设备"]').findAll('option').map(option => option.attributes('value')))
      .toEqual(['device-one', 'device-two'])
    expect(wrapper.text()).toContain('从 NetConsole 服务所在主机')
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled(); expect(api.getNetworkDiagnostics).not.toHaveBeenCalled()
    expect(api.listNetworkDiagnostics).toHaveBeenCalledTimes(1); expect(api.listNetworkDiagnostics).toHaveBeenCalledWith(50)
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
    expect(route.text()).toContain('Windows 系统 ICMP')
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

  it('prevents overlapping progress retries and lets cancellation supersede an in-flight retry', async () => {
    vi.useFakeTimers()
    const late = deferred<NetworkDiagnosticRun>()
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'RUNNING', progressPercent: 0 }))
    api.getNetworkDiagnostics.mockRejectedValueOnce(new Error('诊断进度读取失败'))
      .mockReturnValueOnce(late.promise).mockResolvedValueOnce(run({ state: 'CANCELLED' }))
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    await button(wrapper, '重新读取进度').trigger('click')
    expect(button(wrapper, '重新读取进度').attributes('disabled')).toBeDefined()
    expect(button(wrapper, '重新读取进度').attributes('data-loading')).toBe('true')
    await button(wrapper, '重新读取进度').trigger('click')
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2)
    await button(wrapper, '取消任务').trigger('click'); await flushPromises()
    expect(api.cancelNetworkDiagnostics).toHaveBeenCalledWith('diagnostic-one')
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).toContain('已取消 · 100%')
    late.resolve(run({ state: 'RUNNING', progressPercent: 0 })); await flushPromises()
    expect(wrapper.text()).toContain('已取消 · 100%')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(1400)
    expect(api.startNetworkDiagnostics).toHaveBeenCalledTimes(1)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(3)
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

  it('lets existing read roles discover and reopen retained results after remount without starting diagnostics', async () => {
    const saved = run({ state: 'PARTIAL', failureReason: '保留的部分路径', items: [item({ state: 'FAILED', outcome: 'TIMEOUT' })] })
    api.listNetworkDiagnostics.mockResolvedValue([summary(saved)]); api.getNetworkDiagnostics.mockResolvedValue(saved)
    const reader = await render('ADMIN')
    expect(reader.get('select[aria-label="诊断历史"]').text()).toContain('部分完成 · 1 台设备 · Traceroute 路径')
    expect(api.getNetworkDiagnostics).not.toHaveBeenCalled()
    await reader.get('select[aria-label="诊断历史"]').setValue(saved.id); await flushPromises()
    expect(reader.text()).toContain('保留的部分路径'); expect(reader.text()).toContain('已记录 3 / 30 跳')
    expect(button(reader, '开始诊断').attributes('disabled')).toBeDefined()
    expect(button(reader, '取消任务').attributes('disabled')).toBeDefined()
    reader.unmount()
    const reopened = await render('ADMIN')
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(1)
    await reopened.get('select[aria-label="诊断历史"]').setValue(saved.id); await flushPromises()
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2)
    expect(reopened.text()).toContain('保留的部分路径')
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled(); expect(api.cancelNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('ignores a late detail response after switching history and does not resume the old polling loop', async () => {
    vi.useFakeTimers()
    const old = deferred<NetworkDiagnosticRun>(); const latest = run({ id: 'diagnostic-two', failureReason: '当前选择的历史' })
    api.listNetworkDiagnostics.mockResolvedValue([summary(), summary(latest)])
    api.getNetworkDiagnostics.mockReturnValueOnce(old.promise).mockResolvedValueOnce(latest)
    const wrapper = await render()
    await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one')
    expect(wrapper.text()).toContain('正在读取诊断结果')
    await wrapper.get('select[aria-label="诊断历史"]').setValue(latest.id); await flushPromises()
    expect(wrapper.text()).toContain('当前选择的历史')
    old.resolve(run({ state: 'RUNNING', failureReason: '过期历史不可覆盖' })); await flushPromises()
    await vi.advanceTimersByTimeAsync(1400)
    expect(wrapper.text()).toContain('当前选择的历史'); expect(wrapper.text()).not.toContain('过期历史不可覆盖')
    expect((wrapper.get('select[aria-label="诊断历史"]').element as HTMLSelectElement).value).toBe(latest.id)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2); expect(api.startNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('switches away from a live batch without cancelling it or accepting its late progress', async () => {
    vi.useFakeTimers()
    const late = deferred<NetworkDiagnosticRun>(); const saved = run({ id: 'saved-history', failureReason: '已保存历史' })
    api.listNetworkDiagnostics.mockResolvedValue([summary(saved)])
    api.startNetworkDiagnostics.mockResolvedValue(run({ state: 'RUNNING' }))
    api.getNetworkDiagnostics.mockReturnValueOnce(late.promise).mockResolvedValueOnce(saved)
    const wrapper = await render(); await selectTrace(wrapper); await button(wrapper, '开始诊断').trigger('click'); await flushPromises()
    await wrapper.get('select[aria-label="诊断历史"]').setValue(saved.id); await flushPromises()
    late.resolve(run({ state: 'RUNNING' })); await flushPromises(); await vi.advanceTimersByTimeAsync(1400)
    expect(wrapper.text()).toContain('已保存历史'); expect(wrapper.text()).not.toContain('执行中 · 100%')
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2); expect(api.cancelNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('refreshes summaries without reading details and clears an omitted selection instead of retaining stale evidence', async () => {
    api.listNetworkDiagnostics.mockResolvedValueOnce([summary()]).mockResolvedValueOnce([summary()]).mockResolvedValueOnce([])
    const wrapper = await render(); await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(true)
    await button(wrapper, '刷新历史').trigger('click'); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(true); expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(1)
    await button(wrapper, '刷新历史').trigger('click'); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(false)
    expect((wrapper.get('select[aria-label="诊断历史"]').element as HTMLSelectElement).value).toBe('')
    expect(api.startNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it('does not let an older list response clear a newly selected history', async () => {
    const stale = deferred<NetworkDiagnosticRunSummary[]>()
    api.listNetworkDiagnostics.mockResolvedValueOnce([summary()]).mockReturnValueOnce(stale.promise)
    const wrapper = await render(); await button(wrapper, '刷新历史').trigger('click')
    await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    stale.resolve([]); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(true)
    expect((wrapper.get('select[aria-label="诊断历史"]').element as HTMLSelectElement).value).toBe('diagnostic-one')
    expect(button(wrapper, '刷新历史').attributes('disabled')).toBeUndefined()
  })

  it('retries a failed historical detail read without starting a new batch', async () => {
    api.listNetworkDiagnostics.mockResolvedValue([summary()])
    api.getNetworkDiagnostics.mockRejectedValueOnce(new Error('历史结果暂时不可读')).mockResolvedValueOnce(run())
    const wrapper = await render('ADMIN'); await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('历史结果暂时不可读')
    await button(wrapper, '重新读取结果').trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false); expect(wrapper.find('.diagnostic-results').exists()).toBe(true)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(2); expect(api.startNetworkDiagnostics).not.toHaveBeenCalled()
  })

  it.each([401, 403, 404])('clears inaccessible historical results on detail HTTP %i', async status => {
    api.listNetworkDiagnostics.mockResolvedValue([summary()])
    api.getNetworkDiagnostics.mockResolvedValueOnce(run()).mockRejectedValueOnce(new ApiRequestError(status, 'sensitive server detail'))
    const wrapper = await render(); await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    await wrapper.get('select[aria-label="诊断历史"]').setValue('')
    await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(false)
    expect(wrapper.get('[role="alert"]').text()).toContain('诊断记录已不存在或不可访问，请刷新历史。')
    expect(wrapper.text()).not.toContain('sensitive server detail'); expect(button(wrapper, '重新读取结果')).toBeUndefined()
    expect(wrapper.get('select[aria-label="诊断历史"]').findAll('option')).toHaveLength(1)
  })

  it('clears stale evidence on a denied history refresh and ignores a list from an earlier identity', async () => {
    const stale = deferred<NetworkDiagnosticRunSummary[]>()
    api.listNetworkDiagnostics.mockResolvedValueOnce([summary()]).mockRejectedValueOnce(new ApiRequestError(403, 'denied'))
      .mockReturnValueOnce(stale.promise).mockResolvedValueOnce([])
    const wrapper = await render(); await wrapper.get('select[aria-label="诊断历史"]').setValue('diagnostic-one'); await flushPromises()
    await button(wrapper, '刷新历史').trigger('click'); await flushPromises()
    expect(wrapper.find('.diagnostic-results').exists()).toBe(false)
    expect(wrapper.get('[role="alert"]').text()).toContain('诊断历史读取失败')
    await button(wrapper, '刷新历史').trigger('click')
    useAuthStore().user = { username: 'other-reader', role: 'ADMIN' } as never; await flushPromises()
    stale.resolve([summary()]); await flushPromises()
    expect(wrapper.get('select[aria-label="诊断历史"]').findAll('option')).toHaveLength(1)
    expect(api.getNetworkDiagnostics).toHaveBeenCalledTimes(1); expect(api.startNetworkDiagnostics).not.toHaveBeenCalled()
  })
})
