import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import TopologyView from './TopologyView.vue'

const api = vi.hoisted(() => ({ getDeviceInventory: vi.fn(), getH3cLldpClaims: vi.fn(), getH3cProfiles: vi.fn() }))
vi.mock('../api/client', () => api)
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn() } }))
enableAutoUnmount(afterEach)

const inventory = { page: 1, pageSize: 100, total: 1, hasMore: false, summary: { totalRecords: 1, validDevices: 1, invalidRecords: 0, enabledDevices: 1, disabledDevices: 0, onlineDevices: 1, offlineDevices: 0, unknownStatus: 0 }, items: [{ id: 'device-1', deviceId: 'stable-1', name: '核心交换机', managementAddress: '192.0.2.10', managementIp: '192.0.2.10', port: 22, username: 'reader', vendor: 'H3C', model: 'S5560', deviceType: 'SWITCH', protocol: 'SSH', site: '一号线', siteId: 'site-1', group: '核心', enabled: true, connectionStatus: 'ONLINE', credentialConfigured: true, hostKeyUpdatePolicy: 'MANAGED', terminalEncoding: 'UTF-8', isValid: true, qualityIssues: [], updatedAt: 1_800_000_000_000 }] }
const profile = { id: 'profile-1', host: '192.0.2.10', port: 22, username: 'reader', enabled: true, supportedProtocols: ['SSH'], hostKeyUpdatePolicy: 'MANAGED', allowLegacyAlgorithms: false, terminalEncoding: 'UTF-8', terminalProtocol: 'SSH', allowTelnetFallback: false }
const claims = { profileId: 'profile-1', scopeState: 'AUTHORIZED', isGlobal: false, claimsRevision: 'claims-1', allowedSiteReferences: ['site-1'], factCount: 1, relationCount: 1, relations: [{ profileId: 'profile-1', localDeviceId: 'stable-1', localInterface: 'GigabitEthernet1/0/1', neighborDevice: '接入交换机', neighborPort: 'GigabitEthernet1/0/24', neighborManagementAddress: '192.0.2.11', siteId: 'site-1', source: 'H3C_TASK_RAW:1', capturedMs: 1_800_000_000_000, factRevision: 'fact-1', relationKey: 'stable-1|Gi1/0/1|neighbor', changeKind: 'UNCHANGED' }] }

function render() {
  return mount(TopologyView, { global: { stubs: { NcLayout: { template: '<main><slot name="actions"/><slot/></main>' }, 'el-button': { template: '<button><slot/></button>' }, 'el-icon': { template: '<span><slot/></span>' } } } })
}

describe('TopologyView', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getDeviceInventory.mockResolvedValue(inventory); api.getH3cProfiles.mockResolvedValue([profile]); api.getH3cLldpClaims.mockResolvedValue(claims) })

  it('renders real asset and LLDP data with scope metadata', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('[data-testid="topology-summary"]').text()).toContain('1 台资产')
    expect(wrapper.text()).toContain('核心交换机')
    expect(wrapper.text()).toContain('接入交换机')
    expect(wrapper.text()).toContain('AUTHORIZED')
    expect(api.getDeviceInventory).toHaveBeenCalledWith({ page: 1, pageSize: 100 })
    expect(api.getH3cLldpClaims).toHaveBeenCalledWith('profile-1', { history: false, limit: 1000 })
  })

  it('shows an honest empty state without fabricated counts', async () => {
    api.getDeviceInventory.mockResolvedValue({ ...inventory, total: 0, items: [], summary: { ...inventory.summary, totalRecords: 0, onlineDevices: 0 } })
    api.getH3cProfiles.mockResolvedValue([])
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('[data-testid="topology-empty"]').text()).toContain('暂无可用拓扑或资产数据')
    expect(wrapper.get('[data-testid="topology-summary"]').text()).toContain('0 台资产')
  })

  it('shows service failures as an error state', async () => {
    api.getDeviceInventory.mockRejectedValue(new Error('inventory unavailable'))
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('inventory unavailable')
    expect(wrapper.text()).not.toContain('核心交换机')
  })

  it('keeps successful assets and marks a failed profile as partial', async () => {
    api.getH3cProfiles.mockResolvedValue([profile, { ...profile, id: 'profile-2', host: '192.0.2.12' }])
    api.getH3cLldpClaims.mockResolvedValueOnce(claims).mockRejectedValueOnce(new Error('denied'))
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('核心交换机')
    expect(wrapper.text()).toContain('192.0.2.12 的 LLDP 关系读取失败')
    expect(wrapper.text()).toContain('接入交换机')
  })
})
