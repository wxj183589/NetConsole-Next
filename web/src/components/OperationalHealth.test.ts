import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { h, KeepAlive, nextTick, ref } from 'vue'
import OperationalHealth from './OperationalHealth.vue'

const api = vi.hoisted(() => ({ getOperationalHealth: vi.fn(), recoverOperationalDevice: vi.fn(), getH3cTaskPlanReport: vi.fn() }))
vi.mock('../api/client', () => api)
const unknown = { state: 'UNKNOWN', value: null, unit: '%', capturedMs: null, evidenceIds: [] }
const snapshot = { asOfMs: 1000, freshnessWindowMs: 300000, page: 1, totalDevices: 1, hasMore: false, devices: [{ deviceId: 'a', name: 'fixture a', siteId: 'site-a', profileId: 'profile-a', state: 'UNKNOWN', reachability: unknown, cpuPercent: unknown, memoryPercent: unknown, interfaceDown: unknown, lldpNeighbors: unknown, warnings: [] }], events: [], limitations: ['bounded'] }
const render = (canRecover = true) => mount(OperationalHealth, { props: { canRecover }, global: { stubs: { RouterLink: { props: ['to'], template: '<a><slot /></a>' } } } })
const button = (wrapper: ReturnType<typeof render>, text: string) => wrapper.findAll('button').find(item => item.text() === text)!
describe('OperationalHealth', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getOperationalHealth.mockResolvedValue(structuredClone(snapshot)); api.getH3cTaskPlanReport.mockResolvedValue({ plan: { state: 'QUEUED' }, items: [] }) })
  it('shows UNKNOWN, honest evidence limits and empty event state', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('UNKNOWN')
    expect(wrapper.text()).toContain('采集失败不代表设备离线')
    expect(wrapper.text()).toContain('没有故障事件')
    expect(api.recoverOperationalDevice).not.toHaveBeenCalled()
    expect(button(wrapper, '执行只读恢复').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('freezes device and profile identity for explicit recovery and preserves probe failure history', async () => {
    api.recoverOperationalDevice.mockResolvedValue({ state: 'RECOLLECTION_QUEUED', attempts: [{ ordinal: 1, status: 'CONNECT_TIMEOUT', atMs: 1000 }, { ordinal: 2, status: 'CONNECTED', atMs: 2000 }], planId: 'plan-a', recollectionState: 'QUEUED' })
    api.getH3cTaskPlanReport.mockResolvedValue({ plan: { state: 'SUCCEEDED' }, items: [{ item: { ordinal: 0, state: 'SUCCEEDED' }, run: { state: 'SUCCEEDED' }, commands: [{ index: 0, command: 'display version', state: 'SUCCEEDED', captureStatus: 'COMPLETE', parseStatus: 'PARSED' }] }] })
    const wrapper = render(); await flushPromises()
    await wrapper.get('select').setValue('a'); await button(wrapper, '执行只读恢复').trigger('click'); await flushPromises()
    expect(api.recoverOperationalDevice).toHaveBeenCalledWith({ deviceId: 'a', expectedProfileId: 'profile-a' })
    expect(wrapper.text()).toContain('CONNECT_TIMEOUT'); expect(wrapper.text()).toContain('RECOLLECTION_QUEUED')
    expect(api.getH3cTaskPlanReport).toHaveBeenCalledWith('plan-a')
    expect(wrapper.get('[aria-label="恢复补采结果"]').text()).toContain('Raw=COMPLETE')
    expect(wrapper.text()).toContain('不代表业务恢复完成'); wrapper.unmount()
  })
  it('hides recovery for readers and discards stale inventory after refresh failure', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.find('select').exists()).toBe(false)
    api.getOperationalHealth.mockRejectedValue(new Error('authority unavailable'))
    await button(wrapper, '刷新设备健康').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('authority unavailable')
    expect(wrapper.text()).not.toContain('fixture a'); wrapper.unmount()
  })
  it('exposes collection event evidence and handles empty scoped inventory', async () => {
    api.getOperationalHealth.mockResolvedValueOnce({ ...snapshot, devices: [], totalDevices: 0, events: [{ id: 'event', kind: 'COLLECTION_FAILURE', state: 'FAILED', atMs: 1000, deviceId: 'a', evidenceType: 'TASK_RUN', evidenceId: 'run-a', detail: 'not offline' }] })
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('当前范围没有设备'); expect(wrapper.text()).toContain('TASK_RUN: run-a')
    wrapper.unmount()
  })
  it('invalidates hidden KeepAlive responses and reads current health on reactivation', async () => {
    let resolve!: (value: unknown) => void
    api.getOperationalHealth.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const shown = ref(true)
    const wrapper = mount({ setup: () => () => h(KeepAlive, null, { default: () => shown.value ? h(OperationalHealth) : h('span') }) },
      { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } })
    shown.value = false; await nextTick()
    resolve(snapshot); await flushPromises()
    api.getOperationalHealth.mockResolvedValue({ ...snapshot, devices: [{ ...snapshot.devices[0], name: 'current scoped device' }] })
    shown.value = true; await flushPromises()
    expect(api.getOperationalHealth).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('current scoped device')
    expect(wrapper.text()).not.toContain('fixture a')
    wrapper.unmount()
  })
})
