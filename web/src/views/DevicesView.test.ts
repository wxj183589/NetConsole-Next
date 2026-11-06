import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import DevicesView from './DevicesView.vue'

const api = vi.hoisted(() => ({
  getDeviceSites: vi.fn(), getDeviceGroups: vi.fn(), getTerminalDevices: vi.fn(), getDeviceInventory: vi.fn(),
  getDeviceInventoryDetail: vi.fn(), getModuleStates: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

const profile = { id: 'connection-b', host: '192.0.2.10' }
const item = { id: 'inventory-b', name: '交换机乙', managementIp: profile.host, vendor: 'H3C', deviceType: 'SWITCH', terminalProfileId: profile.id, siteId: 'local', isValid: true, qualityIssues: [] }
const detail = () => ({ item: { ...item }, terminalProfileId: profile.id, terminalProfile: { ...profile }, recentSessions: [], auditEvents: [] })
const wrappers: VueWrapper[] = []
async function render() {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'reader', role: 'ADMIN' }
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/devices', component: DevicesView }, { path: '/config-backups', component: { template: '<div />' } },
  ] })
  await router.push('/devices'); await router.isReady()
  const wrapper = mount(DevicesView, { global: { plugins: [pinia, router], directives: { loading: () => {} }, stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { template: '<button><slot/></button>' },
    'el-dialog': { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot/><slot name="footer"/></section>' },
    'el-icon': { template: '<span />' },
  } } })
  wrappers.push(wrapper); await flushPromises()
  await button(wrapper, '详情').trigger('click'); await flushPromises()
  return { wrapper, router }
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }

describe('device detail configuration backup entry', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getDeviceSites.mockResolvedValue([{ id: 'local', name: '本地局点' }])
    api.getDeviceGroups.mockResolvedValue([])
    api.getTerminalDevices.mockResolvedValue([profile])
    api.getDeviceInventory.mockResolvedValue({ items: [item], total: 1, summary: { totalRecords: 1 } })
    api.getDeviceInventoryDetail.mockResolvedValue(detail())
    api.getModuleStates.mockResolvedValue([{ key: 'config-backups', enabled: true }])
  })
  afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

  it('opens the existing backup page with the inventory UUID rather than connection identity', async () => {
    const { wrapper, router } = await render()
    await button(wrapper, '查看配置备份').trigger('click'); await flushPromises()
    expect(router.currentRoute.value.path).toBe('/config-backups')
    expect(router.currentRoute.value.query).toEqual({ deviceId: 'inventory-b' })
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
  })

  it.each(['Huawei', 'ZTE'])('does not offer H3C commands for %s devices', async vendor => {
    const value = detail(); value.item.vendor = vendor
    api.getDeviceInventoryDetail.mockResolvedValue(value)
    const { wrapper, router } = await render()
    expect(wrapper.text()).toContain('当前仅支持独立管理的 H3C 设备')
    expect(button(wrapper, '查看配置备份').attributes('disabled')).toBeDefined()
    await button(wrapper, '查看配置备份').trigger('click')
    expect(router.currentRoute.value.path).toBe('/devices')
  })

  it('keeps the entry disabled when its module is disabled', async () => {
    api.getModuleStates.mockResolvedValue([{ key: 'config-backups', enabled: false }])
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('配置备份模块已停用')
    expect(button(wrapper, '查看配置备份').attributes('disabled')).toBeDefined()
  })

  it('does not treat an unreadable module state as enabled', async () => {
    api.getModuleStates.mockRejectedValue(new Error('unavailable'))
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('配置备份模块状态暂不可用')
    expect(button(wrapper, '查看配置备份').attributes('disabled')).toBeDefined()
  })

  it('does not offer an unrelated or hidden connection profile', async () => {
    const value = detail(); value.terminalProfile.id = 'connection-a'
    api.getDeviceInventoryDetail.mockResolvedValue(value)
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('该设备尚未关联可用的连接资料')
    expect(button(wrapper, '查看配置备份').attributes('disabled')).toBeDefined()
  })
})
