import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, type DeviceInventoryItem, type H3cTaskPlan, type H3cTaskPlanReport, type WebUser } from '../api/client'
import { useAuthStore } from '../stores/auth'
import DevicesView from './DevicesView.vue'

const api = vi.hoisted(() => ({
  getDeviceSites: vi.fn(), getDeviceGroups: vi.fn(), getTerminalDevices: vi.fn(), getDeviceInventory: vi.fn(),
  getModuleStates: vi.fn(), createDeviceConnectionTests: vi.fn(), getH3cTaskPlanReport: vi.fn(), cancelH3cTaskPlan: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

function device(suffix: string): DeviceInventoryItem {
  return { id: `inventory-${suffix}`, name: `交换机${suffix}`, managementAddress: '192.0.2.10', port: 22,
    username: 'operator', vendor: 'H3C', model: 'fixture', deviceType: 'SWITCH', protocol: 'SSH', site: '本地局点',
    siteId: 'local', group: '', enabled: true, connectionStatus: 'UNKNOWN', credentialConfigured: true,
    hostKeyUpdatePolicy: 'AUTO_ACCEPT', terminalEncoding: 'utf-8', isValid: true, qualityIssues: [], terminalProfileId: `profile-${suffix}` }
}
function inventory(items = [device('a'), device('b')], hasMore = false) {
  return { items, total: items.length, hasMore, summary: { totalRecords: items.length, validDevices: items.length,
    invalidRecords: 0, enabledDevices: items.length, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: items.length } }
}
function plan(state = 'QUEUED', targets = ['b']): H3cTaskPlan {
  return { id: 'plan-b', taskType: 'H3C_SSH_READONLY', state, actorUserId: 'operator', commandPlan: ['display version'],
    claimsRevision: 'revision-1', createdMs: 1, updatedMs: 2, items: targets.map((target, ordinal) => ({
      ordinal, target: `profile-${target}`, definitionId: `definition-${target}`, runId: `run-${target}`, state,
    })) }
}
function report(value = plan()): H3cTaskPlanReport {
  return { plan: value, items: value.items.map(item => ({ item, run: { id: item.runId!, taskType: value.taskType, state: item.state },
    commands: [{ index: 0, command: 'display version', state: item.state }] })) }
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}
const modules = [{ key: 'devices', enabled: true }, { key: 'h3c', enabled: true }]
const wrappers: VueWrapper[] = []
async function render(role: WebUser['role'] = 'SUPERADMIN', cached = false) {
  const pinia = createPinia(); setActivePinia(pinia)
  const auth = useAuthStore(); auth.user = { username: 'operator', role }
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/devices', component: DevicesView }, { path: '/tasks', component: { template: '<div />' } },
  ] })
  await router.push('/devices'); await router.isReady()
  const shown = ref(true)
  const host = cached ? defineComponent({ components: { DevicesView }, setup: () => ({ shown }), template: '<KeepAlive><DevicesView v-if="shown" /></KeepAlive>' }) : DevicesView
  const wrapper = mount(host, { global: { plugins: [pinia, router], directives: { loading: () => {} }, stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { template: '<button><slot/></button>' },
    'el-dialog': { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot/><slot name="footer"/></section>' },
    'el-icon': { template: '<span />' },
  } } })
  wrappers.push(wrapper); await flushPromises()
  return { wrapper, router, auth, shown }
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }
async function choose(wrapper: VueWrapper, suffix = 'b') { await wrapper.get(`input[aria-label="选择设备 交换机${suffix}"]`).setValue(true) }
async function start(wrapper: VueWrapper) { await choose(wrapper); await button(wrapper, '批量测试连接').trigger('click'); await flushPromises() }
function result(wrapper: VueWrapper) { return wrapper.find('[data-testid="device-connection-batch"]') }

describe('inventory batch connection tests', () => {
  beforeEach(() => {
    vi.resetAllMocks(); vi.useFakeTimers()
    api.getDeviceSites.mockResolvedValue([{ id: 'local', name: '本地局点' }, { id: 'other', name: '其他局点' }])
    api.getDeviceGroups.mockResolvedValue([]); api.getTerminalDevices.mockResolvedValue([])
    api.getDeviceInventory.mockResolvedValue(inventory()); api.getModuleStates.mockResolvedValue(modules)
    api.createDeviceConnectionTests.mockResolvedValue(plan()); api.getH3cTaskPlanReport.mockResolvedValue(report())
    api.cancelH3cTaskPlan.mockResolvedValue(plan('CANCELLED'))
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

  it('offers the batch action only to SUPERADMIN and preserves the individual test entry', async () => {
    const { wrapper } = await render('ADMIN')
    expect(button(wrapper, '批量测试连接')).toBeUndefined()
    expect(button(wrapper, '测试连接')).toBeDefined()
    expect(api.createDeviceConnectionTests).not.toHaveBeenCalled()
  })

  it('posts only explicitly selected inventory UUIDs and displays each target and child command', async () => {
    const { wrapper } = await render()
    expect(button(wrapper, '批量测试连接').attributes('disabled')).toBeDefined()
    await start(wrapper)
    expect(api.createDeviceConnectionTests.mock.calls).toEqual([[['inventory-b']]])
    expect(api.getH3cTaskPlanReport.mock.calls).toEqual([['plan-b']])
    expect(result(wrapper).text()).toContain('交换机b')
    expect(result(wrapper).text()).toContain('inventory-b')
    expect(result(wrapper).text()).toContain('profile-b')
    expect(result(wrapper).text()).toContain('run-b')
    expect(result(wrapper).text()).toContain('display version')
    expect(result(wrapper).text()).toContain('已完成 0 / 1')
    expect(button(wrapper, '批量测试连接').attributes('disabled')).toBeDefined()
  })

  it('retains explicit cross-page selections without selecting an entire result set', async () => {
    api.getDeviceInventory.mockResolvedValueOnce(inventory([device('a'), device('b')], true)).mockResolvedValueOnce(inventory([device('c')]))
    api.createDeviceConnectionTests.mockResolvedValue(plan('QUEUED', ['b', 'c']))
    api.getH3cTaskPlanReport.mockResolvedValue(report(plan('QUEUED', ['b', 'c'])))
    const { wrapper } = await render()
    await choose(wrapper); await button(wrapper, '下一页').trigger('click'); await flushPromises(); await choose(wrapper, 'c')
    await button(wrapper, '批量测试连接').trigger('click'); await flushPromises()
    expect(api.createDeviceConnectionTests.mock.calls).toEqual([[['inventory-b', 'inventory-c']]])
    expect(result(wrapper).text()).toContain('交换机b'); expect(result(wrapper).text()).toContain('交换机c')
  })

  it('reads progress automatically, reports failures, and stops polling a completed plan', async () => {
    const complete = plan('PARTIAL_FAILED', ['a', 'b'])
    complete.items[0]!.state = 'SUCCEEDED'; complete.items[1]!.state = 'FAILED'; complete.items[1]!.error = 'AUTH_FAILED'
    api.createDeviceConnectionTests.mockResolvedValue(plan('QUEUED', ['a', 'b']))
    api.getH3cTaskPlanReport.mockResolvedValueOnce(report(plan('QUEUED', ['a', 'b']))).mockResolvedValue(report(complete))
    const { wrapper } = await render()
    await choose(wrapper, 'a'); await start(wrapper)
    await vi.advanceTimersByTimeAsync(1500); await flushPromises()
    expect(result(wrapper).text()).toContain('部分失败'); expect(result(wrapper).text()).toContain('已完成 2 / 2')
    expect(result(wrapper).text()).toContain('账号认证失败，请检查设备连接资料')
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(2)
    expect(api.createDeviceConnectionTests).toHaveBeenCalledTimes(1)
  })

  it('refreshes and cancels the existing plan without creating another connection task', async () => {
    const { wrapper, router } = await render(); await start(wrapper)
    await button(wrapper, '刷新结果').trigger('click'); await flushPromises()
    api.getH3cTaskPlanReport.mockResolvedValue(report(plan('CANCELLED')))
    await button(wrapper, '取消测试').trigger('click'); await flushPromises()
    expect(api.cancelH3cTaskPlan.mock.calls).toEqual([['plan-b']])
    expect(api.createDeviceConnectionTests).toHaveBeenCalledTimes(1)
    expect(result(wrapper).text()).toContain('已取消')
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(3)
    await button(wrapper, '打开任务中心').trigger('click'); await flushPromises()
    expect(router.currentRoute.value.path).toBe('/tasks')
  })

  it('rejects an oversized explicit selection without posting tasks', async () => {
    api.getDeviceInventory.mockResolvedValue(inventory(Array.from({ length: 65 }, (_, index) => device(String(index)))))
    const { wrapper } = await render()
    await wrapper.findAll('input[aria-label="批量当前页全选"]')[0]!.setValue(true)
    expect(wrapper.text()).toContain('已选择 65 台')
    expect(button(wrapper, '批量测试连接').attributes('disabled')).toBeDefined()
    await button(wrapper, '批量测试连接').trigger('click'); await flushPromises()
    expect(api.createDeviceConnectionTests).not.toHaveBeenCalled()
  })

  it.each(['create', 'report'])('clears selection and discards a late %s response after switching Site', async operation => {
    const pendingPlan = deferred<H3cTaskPlan>(); const pendingReport = deferred<H3cTaskPlanReport>()
    if (operation === 'create') api.createDeviceConnectionTests.mockReturnValue(pendingPlan.promise)
    else api.getH3cTaskPlanReport.mockReturnValue(pendingReport.promise)
    const { wrapper } = await render(); await start(wrapper)
    await wrapper.get('.scope-site-select select').setValue('other'); await flushPromises()
    pendingPlan.resolve(plan()); pendingReport.resolve(report(plan('SUCCEEDED'))); await flushPromises()
    expect(wrapper.text()).toContain('已选择 0 台'); expect(result(wrapper).exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(operation === 'create' ? 0 : 1)
  })

  it('creates no task while a required module is disabled', async () => {
    api.getModuleStates.mockResolvedValue([{ key: 'devices', enabled: true }, { key: 'h3c', enabled: false }])
    const { wrapper } = await render(); await start(wrapper)
    expect(api.createDeviceConnectionTests).not.toHaveBeenCalled()
    expect(result(wrapper).text()).toContain('所需模块已停用或不可用')
  })

  it.each(['create', 'report'])('rejects a late %s result when modules are disabled during the request', async operation => {
    const pendingPlan = deferred<H3cTaskPlan>(); const pendingReport = deferred<H3cTaskPlanReport>()
    if (operation === 'create') api.createDeviceConnectionTests.mockReturnValue(pendingPlan.promise)
    else api.getH3cTaskPlanReport.mockReturnValue(pendingReport.promise)
    const { wrapper } = await render(); await start(wrapper)
    api.getModuleStates.mockResolvedValue([{ key: 'devices', enabled: false }, { key: 'h3c', enabled: true }])
    pendingPlan.resolve(plan()); pendingReport.resolve(report(plan('SUCCEEDED'))); await flushPromises()
    expect(result(wrapper).text()).toContain('所需模块已停用或不可用')
    expect(result(wrapper).text()).not.toContain('run-b')
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(operation === 'create' ? 0 : 1)
  })

  it('clears an existing plan and stops polling when module state becomes unreadable', async () => {
    const { wrapper } = await render(); await start(wrapper)
    api.getModuleStates.mockRejectedValue(new Error('offline'))
    await vi.advanceTimersByTimeAsync(1500); await flushPromises()
    expect(result(wrapper).text()).toContain('模块状态暂不可用')
    expect(result(wrapper).text()).not.toContain('run-b')
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(1)
  })

  it.each([[404, '设备或任务不存在'], [409, '当前仅支持已关联可用连接资料的独立 H3C 设备']])('reports preflight failure %s without reporting success or polling', async (status, message) => {
    api.createDeviceConnectionTests.mockRejectedValue(new ApiRequestError(Number(status), 'preflight'))
    const { wrapper } = await render(); await start(wrapper)
    expect(result(wrapper).text()).toContain(message)
    expect(result(wrapper).text()).not.toContain('已完成')
    expect(api.getH3cTaskPlanReport).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.createDeviceConnectionTests).toHaveBeenCalledTimes(1)
  })

  it('discards an in-flight response when privileges are removed', async () => {
    const pending = deferred<H3cTaskPlanReport>(); api.getH3cTaskPlanReport.mockReturnValue(pending.promise)
    const { wrapper, auth } = await render(); await start(wrapper)
    auth.user = { username: 'operator', role: 'ADMIN' }; await flushPromises()
    pending.resolve(report(plan('SUCCEEDED'))); await flushPromises()
    expect(result(wrapper).exists()).toBe(false)
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(1)
  })

  it('invalidates an in-flight create while cached and does not revive it on activation', async () => {
    const pending = deferred<H3cTaskPlan>(); api.createDeviceConnectionTests.mockReturnValue(pending.promise)
    const { wrapper, shown } = await render('SUPERADMIN', true); await start(wrapper)
    shown.value = false; await flushPromises(); pending.resolve(plan()); await flushPromises()
    shown.value = true; await flushPromises()
    expect(result(wrapper).exists()).toBe(false); expect(wrapper.text()).toContain('已选择 0 台')
    expect(api.getH3cTaskPlanReport).not.toHaveBeenCalled()
  })

  it('stops polling when the view unmounts', async () => {
    const { wrapper } = await render(); await start(wrapper); wrapper.unmount()
    await vi.advanceTimersByTimeAsync(15000)
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledTimes(1)
  })
})
