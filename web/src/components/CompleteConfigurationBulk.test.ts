import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, ref } from 'vue'
import CompleteConfigurationBulk from './CompleteConfigurationBulk.vue'

const api = vi.hoisted(() => ({ createCompleteConfigurationBatch: vi.fn(), createCompleteConfigurationSchedule: vi.fn(), getCompleteConfigurationBatch: vi.fn(), getCompleteConfigurationBatches: vi.fn(), getCompleteConfigurationSchedules: vi.fn(), resolveCompleteConfigurationTargets: vi.fn(), runCompleteConfigurationSchedule: vi.fn() }))
const shared = vi.hoisted(() => ({ getDeviceSites: vi.fn(), getWirelessAcDomains: vi.fn(), setConfigBackupScheduleEnabled: vi.fn(), deleteConfigBackupSchedule: vi.fn() }))
vi.mock('../api/completeConfiguration', () => api)
vi.mock('../api/client', () => shared)
const preview = { selectionKind: 'PROFILES', profileIds: ['p1', 'p2'], items: [{ profileId: 'p1', deviceId: 'd1', name: 'AC-1', siteId: 's1', site: 'Site 1', model: 'H3C', address: '192.0.2.1' }, { profileId: 'p2', deviceId: 'd2', name: 'AC-2', siteId: 's1', site: 'Site 1', model: 'H3C', address: '192.0.2.2' }], resolvedMs: 1 }
const partial = { id: 'batch1', completedMs: 2, total: 2, completed: 2, succeeded: 1, failed: 1, state: 'PARTIAL', items: [{ profileId: 'p1', state: 'SUCCEEDED', snapshotId: 'private1' }, { profileId: 'p2', state: 'FAILED', error: 'CONFIG_DEVICE_TIMEOUT' }], configurationKind: 'COMPLETE_RUNNING' }
const plan = { id: 'plan1', profileIds: ['p1', 'p2'], intervalMinutes: 1440, enabled: true, nextRunMs: 1000, lastState: 'NEVER_RUN', configurationKind: 'COMPLETE_RUNNING' }
function render() { return mount(CompleteConfigurationBulk, { props: { selectedProfileIds: ['p1', 'p2'] }, global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } }) }
function button(wrapper: ReturnType<typeof render>, label: string) { return wrapper.findAll('button').find(item => item.text() === label)! }
async function resolve(wrapper: ReturnType<typeof render>) { await button(wrapper, '预览完整备份目标').trigger('click'); await flushPromises() }

describe('complete configuration bulk workflow', () => {
  it('discovers a stored manual batch and retries only its failed device', async () => {
    api.getCompleteConfigurationBatches.mockResolvedValue([partial])
    const wrapper = render(); await flushPromises()
    await button(wrapper, '刷新完整备份批次历史').trigger('click'); await flushPromises()
    await wrapper.get('select[aria-label="完整备份批次历史"]').setValue('batch1')
    await button(wrapper, '查看批次结果').trigger('click'); await flushPromises()
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledWith('batch1')
    expect(wrapper.text()).toContain('CONFIG_DEVICE_TIMEOUT')
    await button(wrapper, '仅重试失败设备').trigger('click'); await flushPromises()
    expect(api.createCompleteConfigurationBatch).toHaveBeenCalledWith(['p2'])
    wrapper.unmount()
  })
  afterEach(() => { vi.useRealTimers() })
  beforeEach(() => {
    vi.resetAllMocks()
    shared.getDeviceSites.mockResolvedValue([{ id: 's1', name: 'Site 1' }])
    shared.getWirelessAcDomains.mockResolvedValue({ items: [{ id: 'domain1', displayName: 'Dual AC' }] })
    api.getCompleteConfigurationSchedules.mockResolvedValue([])
    api.resolveCompleteConfigurationTargets.mockResolvedValue(preview)
    api.createCompleteConfigurationBatch.mockResolvedValue(partial)
    api.getCompleteConfigurationBatch.mockResolvedValue(partial)
    api.createCompleteConfigurationSchedule.mockResolvedValue(plan)
  })

  it('explains full storage and per-device history limits without discarding successful results', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, lastState: 'PARTIAL', lastError: 'CONFIG_COMPLETE_HISTORY_LIMIT', lastBatchId: 'scheduled-batch' }])
    api.getCompleteConfigurationBatch.mockResolvedValue({ ...partial, items: [partial.items[0], { profileId: 'p2', state: 'FAILED', error: 'CONFIG_COMPLETE_STORAGE_LIMIT' }] })
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('此设备完整备份数量已满')
    await button(wrapper, '查看最近执行').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('完整备份存储容量已满')
    expect(wrapper.text()).toContain('历史仍可读取')
    expect(button(wrapper, '查看完整历史')).toBeDefined()
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('requires an explicit target preview before starting private backups', async () => {
    const wrapper = render(); await flushPromises()
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    expect(button(wrapper, '执行完整批量备份')).toBeUndefined()
    await resolve(wrapper)
    expect(api.resolveCompleteConfigurationTargets).toHaveBeenCalledWith({ profileIds: ['p1', 'p2'] })
    expect(wrapper.get('[aria-label="完整备份目标预览"]').text()).toContain('AC-1')
    await button(wrapper, '执行完整批量备份').trigger('click'); await flushPromises()
    expect(api.createCompleteConfigurationBatch).toHaveBeenCalledWith(['p1', 'p2'])
    expect(wrapper.text()).toContain('只提供脱敏副本')
    expect(wrapper.emitted('completed')).toHaveLength(1)
    wrapper.unmount()
  })

  it('resolves an exact Site or Domain rather than silently reusing selected profiles', async () => {
    const wrapper = render(); await flushPromises()
    await wrapper.get('[aria-label="完整配置备份范围"]').setValue('SITE')
    await wrapper.get('[aria-label="完整配置 Site"]').setValue('s1'); await resolve(wrapper)
    expect(api.resolveCompleteConfigurationTargets).toHaveBeenLastCalledWith({ siteId: 's1' })
    await wrapper.get('[aria-label="完整配置备份范围"]').setValue('DOMAIN')
    expect(wrapper.find('[aria-label="完整备份目标预览"]').exists()).toBe(false)
    await wrapper.get('[aria-label="完整配置 Domain"]').setValue('domain1'); await resolve(wrapper)
    expect(api.resolveCompleteConfigurationTargets).toHaveBeenLastCalledWith({ domainId: 'domain1' })
    wrapper.unmount()
  })

  it('discards stale preview results after the device selection changes', async () => {
    let done!: (value: unknown) => void
    api.resolveCompleteConfigurationTargets.mockReturnValueOnce(new Promise(resolve => { done = resolve }))
    const wrapper = render(); await flushPromises()
    await button(wrapper, '预览完整备份目标').trigger('click')
    await wrapper.setProps({ selectedProfileIds: ['other'] })
    done(preview); await flushPromises()
    expect(wrapper.find('[aria-label="完整备份目标预览"]').exists()).toBe(false)
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('retries only failed profiles and exposes private history by profile', async () => {
    const wrapper = render(); await flushPromises(); await resolve(wrapper)
    await button(wrapper, '执行完整批量备份').trigger('click'); await flushPromises()
    await button(wrapper, '查看完整历史').trigger('click')
    expect(wrapper.emitted('inspect')).toEqual([['p1']])
    await button(wrapper, '仅重试失败设备').trigger('click'); await flushPromises()
    expect(api.createCompleteConfigurationBatch).toHaveBeenLastCalledWith(['p2'])
    expect(wrapper.text()).toContain('CONFIG_DEVICE_TIMEOUT')
    wrapper.unmount()
  })

  it('creates a schedule from the frozen preview and clears it when inputs change', async () => {
    const wrapper = render(); await flushPromises(); await resolve(wrapper)
    await wrapper.get('[aria-label="完整备份周期"]').setValue('60')
    api.getCompleteConfigurationSchedules.mockResolvedValue([plan])
    await button(wrapper, '创建完整配置计划').trigger('click'); await flushPromises()
    expect(api.createCompleteConfigurationSchedule).toHaveBeenCalledWith(['p1', 'p2'], 60)
    expect(wrapper.text()).toContain('之后新增的 Site／Domain 成员不会自动加入')
    await wrapper.setProps({ selectedProfileIds: ['p1'] })
    expect(button(wrapper, '创建完整配置计划')).toBeUndefined()
    await button(wrapper, '暂停').trigger('click'); await flushPromises()
    expect(shared.setConfigBackupScheduleEnabled).toHaveBeenCalledWith('plan1', false)
    await button(wrapper, '删除').trigger('click'); await flushPromises()
    expect(shared.deleteConfigBackupSchedule).toHaveBeenCalledWith('plan1')
    wrapper.unmount()
  })

  it('shows preview failure without enabling a batch or exposing server response text', async () => {
    api.resolveCompleteConfigurationTargets.mockRejectedValue(new Error('PRIVATE_SERVER_DATA'))
    const wrapper = render(); await flushPromises(); await resolve(wrapper)
    expect(wrapper.get('[role="alert"]').text()).toContain('无法解析备份范围')
    expect(wrapper.text()).not.toContain('PRIVATE_SERVER_DATA')
    expect(button(wrapper, '执行完整批量备份')).toBeUndefined()
    wrapper.unmount()
  })

  it('keeps profile preview available when Site and Domain choices partially fail', async () => {
    shared.getWirelessAcDomains.mockRejectedValue(new Error('offline'))
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('部分范围或计划未能加载')
    await resolve(wrapper)
    expect(wrapper.find('[aria-label="完整备份目标预览"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('opens the last scheduled batch without starting another capture and exposes frozen targets', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, lastState: 'PARTIAL', lastBatchId: 'scheduled-batch' }])
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('details').text()).toContain('p1')
    await wrapper.get('details button').trigger('click')
    expect(wrapper.emitted('inspect')).toEqual([['p1']])
    await button(wrapper, '查看最近执行').trigger('click'); await flushPromises()
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledWith('scheduled-batch')
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    expect(wrapper.get('[aria-label="完整配置批量进度"]').text()).toContain('计划 plan1 最近执行')
    await button(wrapper, '仅重试失败设备').trigger('click'); await flushPromises()
    expect(api.createCompleteConfigurationBatch).toHaveBeenCalledWith(['p2'])
    wrapper.unmount()
  })

  it('runs a paused plan once and opens its real latest batch without changing enabled state', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, enabled: false, nextRunMs: null }])
    api.runCompleteConfigurationSchedule.mockResolvedValue({ ...plan, enabled: false, lastState: 'RUNNING', lastBatchId: 'manual-plan-batch' })
    const wrapper = render(); await flushPromises()
    await button(wrapper, '立即执行').trigger('click'); await flushPromises()
    expect(api.runCompleteConfigurationSchedule).toHaveBeenCalledWith('plan1')
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledWith('manual-plan-batch')
    expect(shared.setConfigBackupScheduleEnabled).not.toHaveBeenCalled()
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('计划 plan1 最近执行')
    expect(wrapper.text()).toContain('已暂停')
    wrapper.unmount()
  })

  it('shows a safe manual-run error and blocks an already running plan', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([plan])
    api.runCompleteConfigurationSchedule.mockRejectedValue(new Error('PRIVATE_SERVER_DATA'))
    const wrapper = render(); await flushPromises()
    await button(wrapper, '立即执行').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('计划未能立即执行')
    expect(wrapper.text()).not.toContain('PRIVATE_SERVER_DATA')
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, lastState: 'RUNNING' }])
    await button(wrapper, '刷新完整配置计划').trigger('click'); await flushPromises()
    expect(button(wrapper, '立即执行').attributes('disabled')).toBeDefined()
    await button(wrapper, '立即执行').trigger('click'); await flushPromises()
    expect(api.runCompleteConfigurationSchedule).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('preserves an old terminal result while disabling unavailable batch details without a request', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, lastState: 'SUCCEEDED', lastBatchId: 'old-batch', lastBatchAvailable: false }])
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('已完成')
    expect(wrapper.text()).toContain('最近执行明细未保留，可查看各设备完整历史')
    expect(button(wrapper, '查看最近执行').attributes('disabled')).toBeDefined()
    await button(wrapper, '查看最近执行').trigger('click'); await flushPromises()
    expect(api.getCompleteConfigurationBatch).not.toHaveBeenCalled()
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    await wrapper.get('details button').trigger('click')
    expect(wrapper.emitted('inspect')).toEqual([['p1']])
    wrapper.unmount()
  })

  it('reports unavailable scheduled history without leaking raw server content', async () => {
    api.getCompleteConfigurationSchedules.mockResolvedValue([{ ...plan, lastBatchId: 'expired-batch' }])
    api.getCompleteConfigurationBatch.mockRejectedValue(new Error('PRIVATE_SERVER_DATA'))
    const wrapper = render(); await flushPromises()
    await button(wrapper, '查看最近执行').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('记录可能已过期')
    expect(wrapper.text()).not.toContain('PRIVATE_SERVER_DATA')
    expect(api.createCompleteConfigurationBatch).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('keeps one polling chain when manual refresh supersedes an in-flight read', async () => {
    vi.useFakeTimers()
    const running = { ...partial, completedMs: null, state: 'RUNNING' }
    let previous!: (value: unknown) => void
    api.createCompleteConfigurationBatch.mockResolvedValue(running)
    api.getCompleteConfigurationBatch.mockReturnValueOnce(new Promise(resolve => { previous = resolve })).mockResolvedValue(running)
    const wrapper = render(); await flushPromises(); await resolve(wrapper)
    await button(wrapper, '执行完整批量备份').trigger('click'); await flushPromises()
    await button(wrapper, '刷新完整备份进度').trigger('click'); await flushPromises()
    previous(partial); await flushPromises()
    expect(wrapper.emitted('completed')).toBeUndefined()
    expect(vi.getTimerCount()).toBe(1)
    await vi.advanceTimersByTimeAsync(1000)
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledTimes(3)
    expect(vi.getTimerCount()).toBe(1)
    wrapper.unmount()
    expect(vi.getTimerCount()).toBe(0)
  })

  it('pauses polling while a cached page is inactive and resumes on activation', async () => {
    vi.useFakeTimers()
    const running = { ...partial, completedMs: null, state: 'RUNNING' }
    api.createCompleteConfigurationBatch.mockResolvedValue(running)
    api.getCompleteConfigurationBatch.mockResolvedValue(running)
    const visible = ref(true)
    const host = mount(defineComponent({ components: { CompleteConfigurationBulk }, setup: () => ({ visible }), template: '<KeepAlive><CompleteConfigurationBulk v-if="visible" :selected-profile-ids="[\'p1\', \'p2\']" /></KeepAlive>' }), { global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } })
    await flushPromises()
    await host.findAll('button').find(item => item.text() === '预览完整备份目标')!.trigger('click'); await flushPromises()
    await host.findAll('button').find(item => item.text() === '执行完整批量备份')!.trigger('click'); await flushPromises()
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledTimes(1)
    visible.value = false; await flushPromises()
    await vi.advanceTimersByTimeAsync(3000)
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledTimes(1)
    visible.value = true; await flushPromises()
    expect(api.getCompleteConfigurationBatch).toHaveBeenCalledTimes(2)
    host.unmount()
    expect(vi.getTimerCount()).toBe(0)
  })
})
