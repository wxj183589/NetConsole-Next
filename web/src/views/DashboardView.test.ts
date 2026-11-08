import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import DashboardView from './DashboardView.vue'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h, KeepAlive, nextTick, ref } from 'vue'

const api = vi.hoisted(() => ({
  getHealth: vi.fn(), getTasks: vi.fn(), getDeviceInventory: vi.fn(),
  getWirelessAcDomains: vi.fn(), getWirelessApResources: vi.fn(),
}))
const chart = vi.hoisted(() => ({ init: vi.fn(), setOption: vi.fn(), dispose: vi.fn(), resize: vi.fn() }))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('echarts/core', () => ({ use: vi.fn(), init: chart.init }))
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn() } }))

const dashboardOptions = { global: { stubs: {
  OperationalHealth: true, FaultCenter: true,
  RouterLink: { props: ['to'], template: '<a :href="to"><slot/></a>' },
  NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
  'el-button': { template: '<button><slot/></button>' }, 'el-icon': { template: '<span />' },
}, directives: { loading: () => {} } } }

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(accept => { resolve = accept })
  return { promise, resolve }
}

describe('dashboard wireless summary', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    chart.init.mockReturnValue(chart)
    setActivePinia(createPinia())
    api.getHealth.mockResolvedValue({ healthState: 'HEALTHY', resources: {}, unresolvedIntegrityIssues: 0 })
    api.getTasks.mockResolvedValue([{ id: 'failed-1', taskType: 'H3C', state: 'FAILED' }])
    api.getDeviceInventory.mockResolvedValue({ summary: { totalRecords: 1, validDevices: 1, invalidRecords: 0, onlineDevices: 1, offlineDevices: 0, unknownStatus: 0 } })
    api.getWirelessAcDomains.mockResolvedValue({ total: 1, items: [{ id: 'domain-1', members: [{ runtimeState: 'RUNNING_MASTER' }] }] })
    api.getWirelessApResources.mockResolvedValue({ total: 12, items: [] })
  })

  it('shows real AC/AP totals and failed task count', async () => {
    const wrapper = mount(DashboardView, { global: { stubs: {
      OperationalHealth: true, FaultCenter: true, RouterLink: { props: ['to'], template: '<a :href="to"><slot/></a>' }, NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
      'el-button': { template: '<button><slot/></button>' }, 'el-icon': { template: '<span />' },
    }, directives: { loading: () => {} } } })
    await flushPromises()
    expect(wrapper.text()).toContain('无线 AP')
    expect(wrapper.text()).toContain('12')
    expect(wrapper.text()).toContain('AC Domain')
    expect(wrapper.text()).toContain('失败 1')
    expect(api.getWirelessAcDomains).toHaveBeenCalledWith({ page: 1, pageSize: 100 })
    expect(api.getWirelessApResources).toHaveBeenCalledWith({ page: 1, pageSize: 1 })
    wrapper.unmount()
  })

  it('keeps successful API results while health failure leaves health-derived state unknown', async () => {
    api.getHealth.mockRejectedValue(new Error('offline'))
    const wrapper = mount(DashboardView, dashboardOptions)
    expect(wrapper.text()).toContain('告警状态未知')
    expect(wrapper.text()).not.toContain('当前实例没有告警')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('实例健康读取失败')
    expect(wrapper.text()).toContain('实例健康未知，告警信息可能不完整')
    expect(wrapper.get('.dashboard-alert-table').text()).toContain('告警状态未知')
    expect(wrapper.text()).toContain('Raw 写入：未知')
    expect(wrapper.text()).toContain('Syslog：未知')
    expect(wrapper.text()).toContain('H3C：未知')
    expect(wrapper.text()).toContain('未解决 Raw 未知')
    expect(wrapper.text()).not.toContain('未解决 Raw 0')
    const cards = wrapper.findAll('.stat-card')
    expect(cards.find(card => card.text().includes('登记设备'))?.get('.stat-card__value').text()).toBe('1')
    expect(cards.find(card => card.text().includes('无线 AP'))?.get('.stat-card__value').text()).toBe('12')
    expect(cards.find(card => card.text().includes('AC Domain'))?.get('.stat-card__value').text()).toBe('1')
    expect(cards.find(card => card.text().includes('服务告警'))?.get('.stat-card__meta').classes()).not.toContain('good-text')
    expect(wrapper.text()).toContain('失败 1')
    wrapper.unmount()
  })

  it.each([null, undefined, {}])('handles a missing health snapshot (%s) without inventing disabled services', async snapshot => {
    api.getHealth.mockResolvedValue(snapshot)
    const wrapper = mount(DashboardView, dashboardOptions)
    await flushPromises()
    expect(wrapper.text()).toContain('告警状态未知')
    expect(wrapper.text()).toContain('Raw 写入：未知')
    expect(wrapper.text()).toContain('Syslog：未知')
    expect(wrapper.text()).toContain('H3C：未知')
    expect(wrapper.text()).toContain('未解决 Raw 未知')
    expect(wrapper.text()).not.toContain('当前实例没有告警')
    wrapper.unmount()
  })

  it('shows reported false and zero values as known and recovers after a failed refresh', async () => {
    api.getHealth.mockRejectedValueOnce(new Error('offline')).mockResolvedValue({
      healthState: 'HEALTHY', resources: {}, unresolvedIntegrityIssues: 0,
      rawWriteAvailable: true, syslogEnabled: false, h3cCollectionEnabled: true,
    })
    const wrapper = mount(DashboardView, dashboardOptions)
    await flushPromises()
    expect(wrapper.text()).toContain('告警状态未知')
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('当前实例没有告警')
    expect(wrapper.text()).toContain('Raw 写入：可用')
    expect(wrapper.text()).toContain('Syslog：未启用')
    expect(wrapper.text()).toContain('H3C：已启用')
    expect(wrapper.text()).toContain('未解决 Raw 0')
    expect(wrapper.text()).not.toContain('告警状态未知')

    api.getHealth.mockResolvedValue({
      healthState: 'DEGRADED', healthError: 'Raw disk unavailable', unresolvedIntegrityIssues: 3,
      rawWriteAvailable: false, syslogEnabled: true, h3cCollectionEnabled: false,
    })
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Raw 写入：不可用')
    expect(wrapper.text()).toContain('Syslog：已启用')
    expect(wrapper.text()).toContain('H3C：未启用')
    expect(wrapper.text()).toContain('未解决 Raw 3')
    expect(wrapper.get('.dashboard-alert-table').text()).toContain('Raw disk unavailable')
    expect(wrapper.text()).not.toContain('当前实例没有告警')
    wrapper.unmount()
  })

  it('keeps real wireless diagnostics visible when the health snapshot fails', async () => {
    api.getHealth.mockRejectedValue(new Error('offline'))
    api.getWirelessAcDomains.mockRejectedValue(new Error('offline'))
    const wrapper = mount(DashboardView, dashboardOptions)
    await flushPromises()
    expect(wrapper.text()).toContain('实例健康未知，告警信息可能不完整')
    expect(wrapper.get('.dashboard-alert-table').text()).toContain('无线 AC 数据暂不可用')
    expect(wrapper.text()).not.toContain('当前实例没有告警')
    wrapper.unmount()
  })

  it('ignores a superseded response after refresh', async () => {
    const previousHealth = deferred<object>()
    const previousInventory = deferred<object>()
    api.getHealth.mockReturnValueOnce(previousHealth.promise)
    api.getDeviceInventory.mockReturnValueOnce(previousInventory.promise)
    const wrapper = mount(DashboardView, dashboardOptions)
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('当前实例没有告警')
    const chartCalls = chart.init.mock.calls.length

    previousHealth.resolve({ healthState: 'FAULTED', healthError: 'superseded fault', unresolvedIntegrityIssues: 99 })
    previousInventory.resolve({ summary: { totalRecords: 99 } })
    await flushPromises()
    expect(wrapper.text()).toContain('当前实例没有告警')
    expect(wrapper.text()).not.toContain('superseded fault')
    expect(wrapper.text()).not.toContain('未解决 Raw 99')
    expect(wrapper.findAll('.stat-card')[0]!.get('.stat-card__value').text()).toBe('1')
    expect(chart.init).toHaveBeenCalledTimes(chartCalls)
    wrapper.unmount()
  })

  it('does not render a late response after unmount', async () => {
    const pendingHealth = deferred<object>()
    api.getHealth.mockReturnValueOnce(pendingHealth.promise)
    const wrapper = mount(DashboardView, dashboardOptions)
    wrapper.unmount()
    pendingHealth.resolve({ healthState: 'HEALTHY' })
    await flushPromises()
    expect(chart.init).not.toHaveBeenCalled()
  })

  it('discards a response while deactivated and reloads health when activated', async () => {
    const shown = ref(true)
    const pendingHealth = deferred<object>()
    api.getHealth.mockReturnValueOnce(pendingHealth.promise)
    const host = defineComponent({ setup: () => () => h(KeepAlive, null, {
      default: () => shown.value ? h(DashboardView) : null,
    }) })
    const wrapper = mount(host, dashboardOptions)
    shown.value = false
    await nextTick()
    pendingHealth.resolve({ healthState: 'FAULTED', healthError: 'inactive fault' })
    await flushPromises()
    expect(chart.init).not.toHaveBeenCalled()

    shown.value = true
    await flushPromises()
    expect(api.getHealth).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('当前实例没有告警')
    expect(wrapper.text()).not.toContain('inactive fault')
    expect(chart.init).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    expect(chart.dispose).toHaveBeenCalledTimes(1)
  })

  it('keeps inventory visible when wireless APIs fail and exposes the failure state', async () => {
    api.getWirelessAcDomains.mockRejectedValue(new Error('offline'))
    api.getWirelessApResources.mockRejectedValue(new Error('offline'))
    const wrapper = mount(DashboardView, { global: { stubs: {
      OperationalHealth: true, FaultCenter: true, RouterLink: { props: ['to'], template: '<a :href="to"><slot/></a>' }, NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
      'el-button': { template: '<button><slot/></button>' }, 'el-icon': { template: '<span />' },
    }, directives: { loading: () => {} } } })
    await flushPromises()
    expect(wrapper.text()).toContain('登记设备')
    expect(wrapper.text()).toContain('无线 AC 数据暂不可用，AP 数据暂不可用')
    expect(wrapper.text()).toContain('无线 AP')
    wrapper.unmount()
  })
  it('opens existing Syslog/topology routes and focuses local fault history without new requests', async () => {
    api.getHealth.mockResolvedValue({ healthState: 'DEGRADED', healthError: 'Raw integrity issue', resources: {}, unresolvedIntegrityIssues: 1 })
    const wrapper = mount(DashboardView, { attachTo: document.body, global: { stubs: {
      OperationalHealth: true, FaultCenter: true, RouterLink: { props: ['to'], template: '<a :href="to"><slot/></a>' }, NcLayout: { template: '<main><slot/></main>' },
      'el-button': { template: '<button><slot/></button>' }, 'el-icon': { template: '<span />' },
    }, directives: { loading: () => {} } } })
    await flushPromises()
    const nav = wrapper.get('[aria-label="运维快捷操作"]')
    expect(nav.findAll('a').find(a => a.text() === 'Syslog 查询与导出')?.attributes('href')).toBe('/tools')
    expect(nav.findAll('a').find(a => a.text() === '拓扑与资产关系')?.attributes('href')).toBe('/topology')
    const host = wrapper.get('#dashboard-fault-center').element as HTMLElement
    host.scrollIntoView = vi.fn()
    await wrapper.get('button[aria-label="查看故障详情"]').trigger('click')
    expect(document.activeElement).toBe(host)
    expect(host.scrollIntoView).toHaveBeenCalledWith({ block: 'start' })
    await nav.get('a[href="#dashboard-fault-center"]').trigger('click')
    expect(document.activeElement).toBe(host)
    expect(host.scrollIntoView).toHaveBeenCalledWith({ block: 'start' })
    expect(api.getHealth).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
})
