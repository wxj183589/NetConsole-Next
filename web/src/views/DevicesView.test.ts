import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import DevicesView from './DevicesView.vue'

const api = vi.hoisted(() => ({
  getDeviceSites: vi.fn(), getDeviceGroups: vi.fn(), getTerminalDevices: vi.fn(), getDeviceInventory: vi.fn(),
  getDeviceInventoryDetail: vi.fn(), getModuleStates: vi.fn(), getH3cOperationalCapabilities: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

const profile = { id: 'connection-b', host: '192.0.2.10' }
const item = { id: 'inventory-b', name: '交换机乙', managementIp: profile.host, vendor: 'H3C', deviceType: 'SWITCH', terminalProfileId: profile.id, siteId: 'local', isValid: true, qualityIssues: [] }
const detail = () => ({ item: { ...item }, terminalProfileId: profile.id, terminalProfile: { ...profile }, recentSessions: [], auditEvents: [] })
const capabilities = () => ({ profileId: profile.id, model: 'S9850-HCL', version: '7.1.070', release: 'R6555', controllerRole: 'Common', controllerRoleNormalized: 'COMMON', asOf: Date.now(), capabilities: [
  { key: 'AC_ROLE_READ', state: 'VERIFIED', reason: 'COLLECTED_RAW_VERIFIED', evidenceIds: ['raw-role-1'], capturedMs: Date.now(), source: 'NETMIKO_SSH', freshness: 'RECENT' },
  { key: 'INTERFACE_READ', state: 'VERIFIED', reason: 'COLLECTED_RAW_VERIFIED', evidenceIds: ['raw-interface-1'], capturedMs: Date.now(), source: 'NETMIKO_SSH', freshness: 'RECENT' },
  { key: 'LLDP_READ', state: 'STALE', reason: 'EVIDENCE_EXPIRED', evidenceIds: ['raw-lldp-1'], capturedMs: Date.now() - 600000, source: 'NETMIKO_SSH', freshness: 'STALE' },
  { key: 'HA_SWITCH', state: 'UNKNOWN', reason: 'NOT_VERIFIED', evidenceIds: [], capturedMs: null, source: 'NONE', freshness: 'UNKNOWN' },
] })
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
    api.getH3cOperationalCapabilities.mockResolvedValue(capabilities())
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
    expect(api.getH3cOperationalCapabilities).not.toHaveBeenCalled()
  })

  it('shows scoped model, version and read capability evidence without claiming unknown writes are verified', async () => {
    const { wrapper } = await render()
    expect(api.getH3cOperationalCapabilities).toHaveBeenCalledWith(profile.id)
    const evidence = wrapper.get('[data-testid="device-operational-capabilities"]')
    for (const text of ['S9850-HCL', '7.1.070', 'R6555', 'Common → COMMON', '控制器角色读取', 'raw-role-1', 'VERIFIED', 'STALE', 'UNKNOWN', 'NETMIKO_SSH', 'raw-interface-1', 'raw-lldp-1']) expect(evidence.text()).toContain(text)
    expect(wrapper.text()).not.toContain('当前版本没有对应设备详情 API')
  })

  it('shows loading, failure and retry states without retaining earlier successful evidence', async () => {
    const { wrapper } = await render()
    let reject!: (reason: Error) => void
    api.getH3cOperationalCapabilities.mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail }))
    await button(wrapper, '刷新能力证据').trigger('click')
    expect(wrapper.get('[role=status]').text()).toContain('正在读取')
    expect(wrapper.text()).not.toContain('raw-interface-1')
    reject(new Error('offline')); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('读取失败')
    await button(wrapper, '刷新能力证据').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('raw-interface-1')
  })

  it('keeps an empty response explicitly unknown', async () => {
    api.getH3cOperationalCapabilities.mockResolvedValue({ ...capabilities(), model: null, version: null, release: null, capabilities: [] })
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('暂无设备能力证据，状态 UNKNOWN')
  })

  it('rejects capability data for a different profile', async () => {
    api.getH3cOperationalCapabilities.mockResolvedValue({ ...capabilities(), profileId: 'unrelated' })
    const { wrapper } = await render()
    expect(wrapper.get('[role=alert]').text()).toContain('读取失败')
    expect(wrapper.text()).not.toContain('raw-interface-1')
  })

  it('discards a late capability response after selecting another device', async () => {
    let resolve!: (value: ReturnType<typeof capabilities>) => void
    api.getH3cOperationalCapabilities.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const { wrapper } = await render()
    const other = detail(); other.item.name = '另一设备'; other.item.terminalProfileId = 'other-profile'; other.terminalProfileId = 'other-profile'; other.terminalProfile.id = 'other-profile'
    api.getDeviceInventoryDetail.mockResolvedValue(other)
    api.getH3cOperationalCapabilities.mockResolvedValue({ ...capabilities(), profileId: 'other-profile', model: 'other-model', capabilities: [] })
    await button(wrapper, '详情').trigger('click'); await flushPromises()
    resolve(capabilities()); await flushPromises()
    expect(wrapper.text()).toContain('other-model')
    expect(wrapper.text()).not.toContain('raw-interface-1')
  })

  it.each(['site', 'permission'])('discards pending capability evidence when %s changes', async kind => {
    let resolve!: (value: ReturnType<typeof capabilities>) => void
    api.getH3cOperationalCapabilities.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const { wrapper } = await render()
    if (kind === 'site') await wrapper.get('.scope-site-select select').setValue('')
    else useAuthStore().user = { username: 'other-reader', role: 'ADMIN' }
    await flushPromises(); resolve(capabilities()); await flushPromises()
    expect(wrapper.find('[data-testid="device-operational-capabilities"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('raw-interface-1')
  })
})
