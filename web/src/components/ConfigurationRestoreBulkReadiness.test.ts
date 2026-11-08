import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ConfigurationRestoreBulkReadiness from './ConfigurationRestoreBulkReadiness.vue'

const api = vi.hoisted(() => ({ checkRestoreReadiness: vi.fn(), getCompleteConfigurations: vi.fn() }))
vi.mock('../api/configRestoreReadiness', () => ({ checkRestoreReadiness: api.checkRestoreReadiness }))
vi.mock('../api/client', () => ({ getCompleteConfigurations: api.getCompleteConfigurations }))
const backup = { id: 'b1', profileId: 'p1', host: '192.0.2.1', version: 1, model: 'H3C', capturedMs: 1000, sourceVerified: true, restorable: false }
const row = { id: 'row1', profileId: 'p1', backupId: 'b1', state: 'BLOCKED', elapsedMs: 1000, phases: [], blockers: ['SFTP_SUBSYSTEM_UNAVAILABLE'], sourceIntegrityVerified: true, deviceIdentityVerified: true, independentReadbackStable: true, businessBaselineStable: true, sourceSha256: 'source-sha', firstSha256: 'read-sha', secondSha256: 'read-sha' }
function render(profiles = ['p1', 'p2']) { return mount(ConfigurationRestoreBulkReadiness, { props: { selectedProfileIds: profiles }, global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } }) }
describe('multi-device restore preflight', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getCompleteConfigurations.mockImplementation((profile: string) => Promise.resolve([{ ...backup, id: 'b-' + profile, profileId: profile }])); api.checkRestoreReadiness.mockResolvedValue({ id: 'batch', state: 'PARTIAL', createdMs: 1000, items: [row, { ...row, id: 'row2', profileId: 'p2', backupId: 'b-p2', state: 'FAILED', error: 'CONFIG_COMPLETE_SOURCE_UNAVAILABLE' }] }) })
  it('requires explicit source loading before submitting each selected device', async () => {
    const wrapper = render(); await flushPromises()
    expect(api.getCompleteConfigurations).not.toHaveBeenCalled()
    expect(wrapper.findAll('button')[1]!.attributes('disabled')).toBeDefined()
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    await wrapper.findAll('button')[1]!.trigger('click'); await flushPromises()
    expect(api.checkRestoreReadiness).toHaveBeenCalledWith([{ profileId: 'p1', backupId: 'b-p1' }, { profileId: 'p2', backupId: 'b-p2' }])
    expect(wrapper.findAll('article')).toHaveLength(2)
    expect(wrapper.text()).toContain('检查失败')
    expect(wrapper.text()).toContain('设备 SFTP 子系统不可用')
    expect(wrapper.text()).toContain('WRITE 未执行 · READBACK 未执行 · CONFIG_HASH 未执行 · BUSINESS_PROBE 未执行 · ROLLBACK 未执行')
    wrapper.unmount()
  })
  it('allows a different historical complete source for each device', async () => {
    api.getCompleteConfigurations.mockResolvedValue([backup, { ...backup, id: 'older', version: 0 }])
    const wrapper = render(['p1']); await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    await wrapper.get('select').setValue('older')
    await wrapper.findAll('button')[1]!.trigger('click'); await flushPromises()
    expect(api.checkRestoreReadiness).toHaveBeenCalledWith([{ profileId: 'p1', backupId: 'older' }]); wrapper.unmount()
  })
  it('shows a per-device load failure and never silently drops a selected device', async () => {
    api.getCompleteConfigurations.mockImplementation((profile: string) => profile === 'p2' ? Promise.reject(new Error('denied')) : Promise.resolve([backup]))
    const wrapper = render(); await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('完整原件加载失败')
    expect(wrapper.findAll('button')[1]!.attributes('disabled')).toBeDefined()
    expect(api.checkRestoreReadiness).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('clears completed evidence when its historical source changes', async () => {
    api.getCompleteConfigurations.mockResolvedValue([backup, { ...backup, id: 'older', version: 0 }])
    const wrapper = render(['p1']); await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    await wrapper.findAll('button')[1]!.trigger('click'); await flushPromises()
    expect(wrapper.find('article').exists()).toBe(true)
    await wrapper.get('select').setValue('older')
    expect(wrapper.find('article').exists()).toBe(false); wrapper.unmount()
  })
  it('invalidates a pending batch when selected devices change', async () => {
    let resolve!: (value: unknown) => void
    api.checkRestoreReadiness.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render(); await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    await wrapper.findAll('button')[1]!.trigger('click')
    await wrapper.setProps({ selectedProfileIds: ['p3'] })
    resolve({ id: 'batch', createdMs: 1, state: 'BLOCKED', items: [row] }); await flushPromises()
    expect(wrapper.find('[aria-label="多设备恢复检查结果"]').exists()).toBe(false)
    expect(wrapper.find('select').exists()).toBe(false); wrapper.unmount()
  })
  it('rejects oversized selection and excludes corrupt or identity-mismatched sources', async () => {
    const wrapper = render(Array.from({ length: 17 }, (_, index) => 'p' + index))
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('1 至 16')
    expect(api.getCompleteConfigurations).not.toHaveBeenCalled()
    await wrapper.setProps({ selectedProfileIds: ['p1'] })
    api.getCompleteConfigurations.mockResolvedValue([{ ...backup, sourceVerified: false }])
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('暂无有效完整原件')
    expect(wrapper.findAll('button')[1]!.attributes('disabled')).toBeDefined(); wrapper.unmount()
  })
})
