import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import DeviceLldpNeighbors from './DeviceLldpNeighbors.vue'

const api = vi.hoisted(() => ({ getH3cLldpClaims: vi.fn() }))
vi.mock('../api/client', () => api)
enableAutoUnmount(afterEach)
const relation = { profileId: 'p1', localDeviceId: 'd1', localInterface: 'GE1/0/1', neighborDevice: 'AC1', neighborPort: 'GE1/0/2', siteId: 's1', source: 'H3C_TASK_RAW:run', sourceAuthority: 'H3C_TASK_RAW', capturedScopeKey: '[1,"d1","p1","s1"]', capturedMs: 1_800_000_000_000, factRevision: 'revision1', relationKey: 'relation1', changeKind: 'UNCHANGED' }
const response = () => ({ profileId: 'p1', scopeState: 'AUTHORIZED', claimsRevision: 'claim1', relationCount: 1, relations: [relation] })
const render = (profileId = 'p1') => mount(DeviceLldpNeighbors, { props: { profileId, deviceId: 'd1', siteId: 's1' }, global: { stubs: { 'el-button': { template: '<button><slot/></button>' } } } })

describe('device LLDP neighbors', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getH3cLldpClaims.mockResolvedValue(response()) })
  it('shows exact device/profile/Site provenance and topology navigation', async () => {
    const wrapper = render(); await flushPromises()
    expect(api.getH3cLldpClaims).toHaveBeenCalledWith('p1', { history: false, limit: 1000 })
    for (const text of ['GE1/0/1', 'AC1', 'GE1/0/2', 'H3C_TASK_RAW:run', 'revision1', relation.capturedScopeKey, 'claim1', '地址未知']) expect(wrapper.text()).toContain(text)
    expect(wrapper.text()).toContain('不推断上下游方向')
    await wrapper.findAll('button')[1]!.trigger('click')
    expect(wrapper.emitted('openTopology')).toHaveLength(1)
  })
  it('excludes another device, profile and captured Site', async () => {
    api.getH3cLldpClaims.mockResolvedValue({ ...response(), relations: [
      { ...relation, localDeviceId: 'd2' }, { ...relation, profileId: 'p2' }, { ...relation, siteId: 's2' },
    ] })
    const wrapper = render(); await flushPromises()
    expect(wrapper.find('table').exists()).toBe(false)
    expect(wrapper.text()).toContain('暂无 LLDP 邻居事实')
  })
  it('clears previous evidence while loading and on failure then allows retry', async () => {
    const wrapper = render(); await flushPromises()
    let reject!: (error: Error) => void
    api.getH3cLldpClaims.mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail }))
    await wrapper.findAll('button')[0]!.trigger('click')
    expect(wrapper.get('[role=status]').text()).toContain('正在读取')
    expect(wrapper.text()).not.toContain('H3C_TASK_RAW:run')
    reject(new Error('denied')); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('读取失败')
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(wrapper.find('table').exists()).toBe(true)
  })
  it('discards a late response after selection changes', async () => {
    let resolve!: (value: ReturnType<typeof response>) => void
    api.getH3cLldpClaims.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render()
    api.getH3cLldpClaims.mockResolvedValue({ ...response(), profileId: 'p2', relations: [] })
    await wrapper.setProps({ profileId: 'p2' }); await flushPromises()
    resolve(response()); await flushPromises()
    expect(wrapper.text()).not.toContain('H3C_TASK_RAW:run')
    expect(wrapper.text()).toContain('暂无 LLDP 邻居事实')
  })
  it('does not request an unbound profile and rejects mismatched response identity', async () => {
    const wrapper = render(''); await flushPromises()
    expect(api.getH3cLldpClaims).not.toHaveBeenCalled()
    api.getH3cLldpClaims.mockResolvedValue({ ...response(), profileId: 'unrelated' })
    await wrapper.setProps({ profileId: 'p1' }); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toContain('读取失败')
    expect(wrapper.find('table').exists()).toBe(false)
  })
})
