import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { h, KeepAlive, nextTick, ref } from 'vue'
import FaultCenter from './FaultCenter.vue'

const api = vi.hoisted(() => ({ getFaults: vi.fn(), acknowledgeFault: vi.fn(), verifyFault: vi.fn(), setFaultPolicy: vi.fn(), syncFaults: vi.fn() }))
vi.mock('../api/faults', () => api)
const incident = { id: 'fault-a', deviceId: 'device-a', profileId: 'profile-a', siteId: 'site-a', kind: 'INTERFACE_DOWN', resourceKey: 'GigabitEthernet1/0/1', severity: 'WARNING', state: 'OPEN', firstSeenMs: 1000, lastSeenMs: 2000, evidenceType: 'FACT', evidenceId: 'fact-a', detail: 'observed DOWN', occurrences: 2, history: [{ atMs: 2000, action: 'DETECTED', actorUserId: 'actor', evidenceId: 'fact-a', detail: 'Raw verified' }] }
const result = { page: 1, pageSize: 25, total: 1, hasMore: false, items: [incident], policies: [], limitations: ['1000 incidents / 50 transitions'] }
const render = (canRecover = true) => mount(FaultCenter, { props: { canRecover } })
const button = (wrapper: ReturnType<typeof render>, text: string) => wrapper.findAll('button').find(b => b.text() === text)!

describe('FaultCenter', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getFaults.mockResolvedValue(structuredClone(result)); api.setFaultPolicy.mockResolvedValue({ state: 'SCHEDULED' }); api.verifyFault.mockResolvedValue({ state: 'RECOVERY_NOT_VERIFIED', detail: '缺少恢复证据' }) })
  it('loads durable events without admitting device actions and expands evidence history', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('OPEN'); expect(wrapper.text()).toContain('2 次有效观测')
    expect(api.setFaultPolicy).not.toHaveBeenCalled(); expect(api.syncFaults).not.toHaveBeenCalled()
    await button(wrapper, '查看处理历史').trigger('click')
    expect(wrapper.text()).toContain('Raw verified'); expect(wrapper.text()).toContain('默认关闭')
    wrapper.unmount()
  })
  it('confirms and verifies without falsely changing lifecycle from a blocked verification', async () => {
    const wrapper = render(); await flushPromises()
    await button(wrapper, '确认故障').trigger('click'); await flushPromises()
    expect(api.acknowledgeFault).toHaveBeenCalledWith('fault-a')
    await button(wrapper, '核验恢复').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('RECOVERY_NOT_VERIFIED'); expect(wrapper.text()).toContain('OPEN')
    wrapper.unmount()
  })
  it('requires explicit bounded target policy and shows the durable plan rather than recovery success', async () => {
    const wrapper = render(); await flushPromises(); await button(wrapper, '查看处理历史').trigger('click')
    await button(wrapper, '启用有限只读补采').trigger('click'); await flushPromises()
    expect(api.setFaultPolicy).toHaveBeenCalledWith(incident, true, 5, 3)
    expect(wrapper.text()).toContain('SCHEDULED'); expect(wrapper.text()).toContain('不代表业务恢复')
    api.getFaults.mockResolvedValue({ ...result, policies: [{ deviceId: 'device-a', state: 'AWAITING_RECOLLECTION', enabled: true, attempts: 1, maximumAttempts: 3, planId: 'plan-a', nextRunMs: 10000 }] })
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.get('a').attributes('href')).toContain('/task-plans/plan-a/report')
    expect(button(wrapper, '启用有限只读补采').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('hides mutation controls from readers and keeps history query usable', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.text()).not.toContain('确认故障'); expect(wrapper.text()).not.toContain('同步当前证据')
    await wrapper.findAll('select')[0]!.setValue('RESOLVED')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(api.getFaults).toHaveBeenLastCalledWith(1, expect.objectContaining({ state: 'RESOLVED' }))
    wrapper.unmount()
  })
  it('clears stale records on history error and renders an empty scope', async () => {
    const wrapper = render(); await flushPromises()
    api.getFaults.mockRejectedValueOnce(new Error('store unavailable'))
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('store unavailable'); expect(wrapper.text()).not.toContain('observed DOWN')
    api.getFaults.mockResolvedValueOnce({ ...result, total: 0, items: [] })
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('没有故障记录'); wrapper.unmount()
  })
  it('invalidates a hidden response and refreshes when KeepAlive reactivates', async () => {
    let complete!: (value: typeof result) => void
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const shown = ref(true)
    const wrapper = mount({ render: () => h(KeepAlive, null, { default: () => shown.value ? h(FaultCenter) : h('div', 'hidden') }) })
    await nextTick(); shown.value = false; await nextTick(); complete(result); await flushPromises()
    shown.value = true; await flushPromises()
    expect(api.getFaults).toHaveBeenCalledTimes(2); expect(wrapper.text()).toContain('故障中心')
    wrapper.unmount()
  })
})
