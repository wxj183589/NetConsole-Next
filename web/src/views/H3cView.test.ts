import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { H3cFact } from '../api/client'
import H3cView from './H3cView.vue'

const api = vi.hoisted(() => ({
  getH3cFacts: vi.fn(), getH3cLldpClaims: vi.fn(), getH3cLldpRelations: vi.fn(), getH3cProfiles: vi.fn(),
  createH3cTaskDefinition: vi.fn(), startH3cTaskDefinition: vi.fn(),
}))
vi.mock('../api/client', () => api)
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn() } }))
enableAutoUnmount(afterEach)

const profile = { id: 'profile-one', host: '192.0.2.10', port: 22, username: 'reader' }
const row = { vlanId: 10, name: '实采 VLAN', description: '设备输出', type: 'Static', routeInterface: true,
  taggedPorts: ['GigabitEthernet1/0/1'], untaggedPorts: ['GigabitEthernet1/0/2'] }
function fact(id = 'current', changes: Partial<H3cFact> = {}, payload: Record<string, unknown> = {}): H3cFact {
  return { id, profileId: profile.id, kind: 'vlan.table', identityKey: 'default', status: 'STALE',
    source: `H3C_TASK_RAW:${id}`, capturedMs: 1_800_000_000_000, isCurrent: true, collectedRawVerified: true, dataState: 'COLLECTED_RAW_VERIFIED',
    payloadJson: JSON.stringify({ command: 'display vlan all', status: 'PARSED', vlans: [row], notice: null, ...payload }), ...changes }
}
function render() {
  return mount(H3cView, { global: { stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { template: '<button><slot/></button>' },
  } } })
}
function button(wrapper: VueWrapper, label: string) { return wrapper.findAll('button').find(item => item.text() === label)! }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve } }

describe('H3C VLAN collected snapshots', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getH3cProfiles.mockResolvedValue([profile])
    api.getH3cFacts.mockResolvedValue([fact()])
    api.getH3cLldpRelations.mockResolvedValue([])
    api.getH3cLldpClaims.mockResolvedValue(null)
  })

  it('shows collected VLAN columns with time and source without starting collection', async () => {
    const wrapper = render(); await flushPromises()
    const snapshot = wrapper.get('.vlan-snapshot')
    for (const value of ['当前快照', '解析完成', '实采 VLAN', '设备输出', 'Static', 'GigabitEthernet1/0/1', 'GigabitEthernet1/0/2', 'H3C_TASK_RAW:current'])
      expect(snapshot.text()).toContain(value)
    expect(snapshot.findAll('tbody td').map(cell => cell.text())).toEqual([
      '10', '实采 VLAN', '设备输出', 'Static', '有', 'GigabitEthernet1/0/1', 'GigabitEthernet1/0/2',
    ])
    expect(snapshot.text()).toContain(new Date(fact().capturedMs).toLocaleString('zh-CN', { hour12: false }))
    expect(snapshot.text()).toContain('不代表设备当前实时状态')
    expect(snapshot.text()).not.toContain('设备离线')
    expect(api.createH3cTaskDefinition).not.toHaveBeenCalled()
    expect(api.startH3cTaskDefinition).not.toHaveBeenCalled()
  })

  it('keeps whole historical snapshots separate and filters history from current mode', async () => {
    const older = fact('older', { isCurrent: false, capturedMs: 1_700_000_000_000 }, { vlans: [{ ...row, name: '历史 VLAN' }] })
    api.getH3cFacts.mockResolvedValue([older, fact()])
    const wrapper = render(); await flushPromises()
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(1)
    expect(wrapper.get('.vlan-facts').text()).not.toContain('历史 VLAN')
    await button(wrapper, '查看历史').trigger('click'); await flushPromises()
    expect(api.getH3cFacts).toHaveBeenLastCalledWith(profile.id, { kind: 'vlan.table', history: true, limit: 500 })
    const snapshots = wrapper.findAll('.vlan-snapshot')
    expect(snapshots).toHaveLength(2)
    expect(snapshots[0].text()).toContain('当前快照')
    expect(snapshots[0].text()).not.toContain('历史 VLAN')
    expect(snapshots[1].text()).toContain('历史快照')
    expect(snapshots[1].text()).toContain('历史 VLAN')
    expect(snapshots[1].findAll('tbody tr')).toHaveLength(1)
  })

  it.each([
    ['PARTIAL', 'PARSED', '部分解析', true],
    ['STALE', 'PARTIAL', '部分解析', true],
    ['UNSUPPORTED', 'PARSED', '未识别或不支持', false],
    ['STALE', 'UNRECOGNIZED', '未识别或不支持', false],
    ['EMPTY', 'PARSED', '无数据', false],
    ['STALE', 'EMPTY', '无数据', false],
    ['FAILED', 'PARSED', '采集失败', false],
  ])('does not present %s / %s as parsed success even with verified Raw', async (status, parseStatus, label, showRows) => {
    api.getH3cFacts.mockResolvedValue([fact('state', { status }, { status: parseStatus, notice: '本次快照说明' })])
    const wrapper = render(); await flushPromises()
    const snapshot = wrapper.get('.vlan-snapshot')
    expect(snapshot.get('.state-pill').text()).toBe(label)
    expect(snapshot.find('.state-pill.good').exists()).toBe(false)
    expect(snapshot.text()).toContain('Raw 完整性已校验，解析结果以上述状态为准')
    expect(snapshot.text()).toContain('本次快照说明')
    expect(snapshot.text().includes('实采 VLAN')).toBe(showRows)
  })

  it('never falls back to an older successful snapshot for a current failed snapshot', async () => {
    api.getH3cFacts.mockResolvedValue([
      fact('failed', { status: 'FAILED' }),
      fact('previous', { isCurrent: false, capturedMs: 1_700_000_000_000 }, { vlans: [{ ...row, name: '旧成功 VLAN' }] }),
    ])
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('.vlan-facts').text()).not.toContain('旧成功 VLAN')
    await button(wrapper, '查看历史').trigger('click'); await flushPromises()
    const snapshots = wrapper.findAll('.vlan-snapshot')
    expect(snapshots[0].text()).toContain('采集失败')
    expect(snapshots[0].text()).not.toContain('旧成功 VLAN')
    expect(snapshots[1].text()).toContain('旧成功 VLAN')
    expect(snapshots[1].text()).toContain('历史快照')
  })

  it('clears history immediately when changing mode and shows read failures without old rows', async () => {
    const older = fact('older', { isCurrent: false }, { vlans: [{ ...row, name: '旧历史 VLAN' }] })
    api.getH3cFacts.mockResolvedValue([older, fact()])
    const wrapper = render(); await flushPromises()
    await button(wrapper, '查看历史').trigger('click'); await flushPromises()
    expect(wrapper.get('.vlan-facts').text()).toContain('旧历史 VLAN')
    api.getH3cFacts.mockRejectedValue(new Error('synthetic read failure'))
    await button(wrapper, '仅当前').trigger('click')
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(0)
    await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('事实读取失败')
    expect(wrapper.get('.vlan-facts').text()).toContain('VLAN 事实暂不可用')
    expect(wrapper.text()).not.toContain('旧历史 VLAN')
  })

  it('isolates late history responses after returning to current mode', async () => {
    const old = deferred<H3cFact[]>()
    const wrapper = render(); await flushPromises()
    api.getH3cFacts.mockImplementation((_id: string, options: { history: boolean }) => options.history ? old.promise : Promise.resolve([fact()]))
    await button(wrapper, '查看历史').trigger('click')
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(0)
    await button(wrapper, '仅当前').trigger('click'); await flushPromises()
    old.resolve([fact('late', { isCurrent: false }, { vlans: [{ ...row, name: '迟到历史 VLAN' }] })]); await flushPromises()
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(1)
    expect(wrapper.get('.vlan-facts').text()).toContain('实采 VLAN')
    expect(wrapper.text()).not.toContain('迟到历史 VLAN')
  })

  it('isolates late facts after selecting another profile', async () => {
    const other = { ...profile, id: 'profile-two', host: '192.0.2.11' }
    const old = deferred<H3cFact[]>()
    api.getH3cProfiles.mockResolvedValue([profile, other])
    api.getH3cFacts.mockImplementation((id: string) => id === profile.id ? old.promise : Promise.resolve([
      fact('other', { profileId: other.id }, { vlans: [{ ...row, name: '另一个设备 VLAN' }] }),
    ]))
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button').filter(item => item.text() === '查看事实')[1].trigger('click'); await flushPromises()
    old.resolve([fact()]); await flushPromises()
    expect(wrapper.get('.vlan-facts').text()).toContain('另一个设备 VLAN')
    expect(wrapper.get('.vlan-facts').text()).not.toContain('实采 VLAN')
  })

  it('clears previous VLAN data when profile refresh fails', async () => {
    const wrapper = render(); await flushPromises()
    api.getH3cProfiles.mockRejectedValue(new Error('synthetic denied'))
    await button(wrapper, '刷新').trigger('click'); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('设备资料读取失败')
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(0)
    expect(wrapper.text()).not.toContain('实采 VLAN')
  })

  it.each(['null', '[]', '{broken', JSON.stringify({ command: 'display vlan all', status: 'PARSED', vlans: [null] })])(
    'shows malformed VLAN payload %s without success or a render error', async payloadJson => {
      api.getH3cFacts.mockResolvedValue([fact('malformed', { payloadJson })])
      const wrapper = render(); await flushPromises()
      expect(wrapper.get('.vlan-snapshot .state-pill').classes()).not.toContain('good')
      expect(wrapper.get('.vlan-snapshot').text()).not.toContain('实采 VLAN')
    },
  )

  it('escapes device-provided names, descriptions, ports, notices and sources as text', async () => {
    const text = '<img src=x onerror="alert(1)"><script>alert(2)</script>'
    api.getH3cFacts.mockResolvedValue([fact('malicious', { source: text }, {
      notice: text, vlans: [{ ...row, name: text, description: text, type: text, taggedPorts: [text], untaggedPorts: [text] }],
    })])
    const wrapper = render(); await flushPromises()
    const snapshot = wrapper.get('.vlan-snapshot')
    expect(snapshot.text()).toContain(text)
    expect(snapshot.find('img').exists()).toBe(false)
    expect(snapshot.find('script').exists()).toBe(false)
    expect(snapshot.html()).toContain('&lt;img')
  })

  it('shows no collected snapshots when only planning-like facts exist', async () => {
    api.getH3cFacts.mockResolvedValue([fact('plan', { kind: 'vlan.plan' })])
    const wrapper = render(); await flushPromises()
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(0)
    expect(wrapper.get('.vlan-facts').text()).toContain('规划 VLAN 不作为实采数据展示')
  })

  it('reads VLAN snapshots independently when the general facts limit is filled by interface facts', async () => {
    const interfaces = Array.from({ length: 100 }, (_, index) => fact(`interface-${index}`, {
      kind: 'interface', identityKey: `GigabitEthernet1/0/${index + 1}`, payloadJson: JSON.stringify({ description: '接口事实' }),
    }))
    api.getH3cFacts.mockImplementation((_id: string, options: { kind?: string }) => Promise.resolve(
      options.kind === 'vlan.table' ? [fact()] : interfaces,
    ))
    const wrapper = render(); await flushPromises()
    expect(api.getH3cFacts).toHaveBeenCalledWith(profile.id, { history: false, limit: 100 })
    expect(api.getH3cFacts).toHaveBeenCalledWith(profile.id, { kind: 'vlan.table', history: false, limit: 100 })
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(1)
    expect(wrapper.get('.vlan-facts').text()).toContain('实采 VLAN')
    expect(wrapper.get('.inventory-summary').text()).toContain('101 条事实')
  })

  it('uses only the dedicated VLAN result and counts overlapping general facts once', async () => {
    api.getH3cFacts.mockImplementation((_id: string, options: { kind?: string }) => Promise.resolve(
      options.kind === 'vlan.table' ? [fact()] : [fact(), fact('general-only', {}, { vlans: [{ ...row, name: '通用列表旧快照' }] })],
    ))
    const wrapper = render(); await flushPromises()
    expect(wrapper.findAll('.vlan-snapshot')).toHaveLength(1)
    expect(wrapper.get('.vlan-facts').text()).not.toContain('通用列表旧快照')
    expect(wrapper.get('.inventory-summary').text()).toContain('1 条事实')
  })
})
