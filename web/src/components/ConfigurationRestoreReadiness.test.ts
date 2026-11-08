import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ConfigurationRestoreReadiness from './ConfigurationRestoreReadiness.vue'

const api = vi.hoisted(() => ({ checkRestoreReadiness: vi.fn(), getRestoreReadinessHistory: vi.fn() }))
vi.mock('../api/configRestoreReadiness', () => api)
const row = { id: 'r1', batchId: 'batch', profileId: 'p1', backupId: 'backup1', state: 'BLOCKED', createdMs: 1000, completedMs: 2000, elapsedMs: 1000, sourceIntegrityVerified: true, deviceIdentityVerified: true, independentReadbackStable: true, businessBaselineStable: true, sourceMatchesCurrent: true, sourceSha256: 'source-sha', firstSha256: 'full-sha', secondSha256: 'full-sha', businessBaselineSha256: 'business-sha', checkpointState: 'NO_COMMIT_POINT', capabilities: { nativeFileReplaceAdvertised: true, sftpAvailable: false }, blockers: ['SFTP_SUBSYSTEM_UNAVAILABLE', 'NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED'], phases: [{ name: 'INDEPENDENT_RECONNECT', state: 'PASS', elapsedMs: 200 }], writePass: false, readbackPass: false, configHashPass: false, businessProbePass: false, rollbackPass: false }
function render(backupId = 'backup1') { return mount(ConfigurationRestoreReadiness, { props: { profileId: 'p1', backupId }, global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } }) }
describe('configuration restore readiness', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getRestoreReadinessHistory.mockResolvedValue([]); api.checkRestoreReadiness.mockResolvedValue({ items: [row] }) })
  it('loads history and requires a selected source without writing', async () => {
    const wrapper = render(''); await flushPromises()
    expect(wrapper.text()).toContain('暂无恢复前检查记录')
    expect(wrapper.findAll('button')[0]!.attributes('disabled')).toBeDefined()
    expect(api.checkRestoreReadiness).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('keeps actual restore acceptance separate from successful preflight evidence', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(api.checkRestoreReadiness).toHaveBeenCalledWith([{ profileId: 'p1', backupId: 'backup1' }])
    expect(wrapper.text()).toContain('独立完整重读：稳定')
    expect(wrapper.text()).toContain('原生文件替换命令：设备已提供')
    expect(wrapper.text()).toContain('设备 SFTP 子系统不可用')
    expect(wrapper.text()).toContain('WRITE 未执行 · READBACK 未执行 · CONFIG_HASH 未执行 · BUSINESS_PROBE 未执行 · ROLLBACK 未执行')
    expect(wrapper.text()).toContain('source-sha'); expect(wrapper.text()).toContain('business-sha')
    expect(wrapper.findAll('details')).toHaveLength(1); wrapper.unmount()
  })
  it('exposes a history error and allows a retry', async () => {
    api.getRestoreReadinessHistory.mockRejectedValueOnce(new Error('offline'))
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('历史加载失败')
    api.getRestoreReadinessHistory.mockResolvedValue([row])
    await wrapper.findAll('button')[1]!.trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('检查已完成，恢复受阻'); wrapper.unmount()
  })
  it('shows live persisted progress without reporting a service restart', async () => {
    api.getRestoreReadinessHistory.mockResolvedValue([{ ...row, state: 'RUNNING', completedMs: null }])
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('summary').text()).toContain('正在检查设备')
    expect(wrapper.text()).not.toContain('服务重启中断'); wrapper.unmount()
  })
  it('does not publish a stale device response after switching devices', async () => {
    let resolve!: (value: unknown) => void
    api.checkRestoreReadiness.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button')[0]!.trigger('click')
    await wrapper.setProps({ profileId: 'p2', backupId: 'backup2' }); await flushPromises()
    resolve({ items: [row] }); await flushPromises()
    expect(wrapper.find('details').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('source-sha'); wrapper.unmount()
  })
  it('invalidates a pending check when its selected historical original changes', async () => {
    let resolve!: (value: unknown) => void
    api.checkRestoreReadiness.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button')[0]!.trigger('click')
    await wrapper.setProps({ backupId: 'new-source' }); await flushPromises()
    resolve({ items: [row] }); await flushPromises()
    expect(wrapper.find('details').exists()).toBe(false)
    expect(wrapper.findAll('button')[0]!.attributes('disabled')).toBeUndefined(); wrapper.unmount()
  })
  it('shows request failure without creating a fake completed history row', async () => {
    api.checkRestoreReadiness.mockRejectedValueOnce(new Error('denied'))
    const wrapper = render(); await flushPromises()
    await wrapper.findAll('button')[0]!.trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('未完成')
    expect(wrapper.find('details').exists()).toBe(false); wrapper.unmount()
  })
})
