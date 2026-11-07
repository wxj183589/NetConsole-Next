import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import DashboardView from './DashboardView.vue'
import { createPinia, setActivePinia } from 'pinia'

const api = vi.hoisted(() => ({
  getHealth: vi.fn(), getTasks: vi.fn(), getDeviceInventory: vi.fn(),
  getWirelessAcDomains: vi.fn(), getWirelessApResources: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('echarts', () => ({ init: () => ({ setOption: vi.fn(), dispose: vi.fn(), resize: vi.fn() }) }))
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn() } }))

describe('dashboard wireless summary', () => {
  beforeEach(() => {
    vi.resetAllMocks()
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
    const wrapper = mount(DashboardView, { attachTo: document.body, global: { stubs: {
      OperationalHealth: true, FaultCenter: true, RouterLink: { props: ['to'], template: '<a :href="to"><slot/></a>' }, NcLayout: { template: '<main><slot/></main>' },
      'el-button': true, 'el-icon': true,
    }, directives: { loading: () => {} } } })
    await flushPromises()
    const nav = wrapper.get('[aria-label="运维快捷操作"]')
    expect(nav.findAll('a').find(a => a.text() === 'Syslog 查询与导出')?.attributes('href')).toBe('/tools')
    expect(nav.findAll('a').find(a => a.text() === '拓扑与资产关系')?.attributes('href')).toBe('/topology')
    const host = wrapper.get('#dashboard-fault-center').element as HTMLElement
    host.scrollIntoView = vi.fn()
    await nav.get('a[href="#dashboard-fault-center"]').trigger('click')
    expect(document.activeElement).toBe(host)
    expect(host.scrollIntoView).toHaveBeenCalledWith({ block: 'start' })
    expect(api.getHealth).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
})
