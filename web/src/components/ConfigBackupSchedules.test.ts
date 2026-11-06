import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, type ConfigBackupSchedule } from '../api/client'
import ConfigBackupSchedules from './ConfigBackupSchedules.vue'

const api = vi.hoisted(() => ({ getConfigBackupSchedules: vi.fn(), createConfigBackupSchedule: vi.fn(), setConfigBackupScheduleEnabled: vi.fn(), deleteConfigBackupSchedule: vi.fn() }))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
const schedule: ConfigBackupSchedule = { id: 'schedule-b', ownerUserId: 'owner', profileIds: ['profile-b'], configurationKind: 'SAVED', intervalMinutes: 1440, enabled: true, createdMs: 1000, nextRunMs: 86401000, lastState: 'SUCCEEDED', lastBatchId: 'batch-b' }
const wrappers: VueWrapper[] = []
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve } }
async function render(selectedProfileIds = ['profile-b']) {
  const wrapper = mount(ConfigBackupSchedules, { props: { selectedProfileIds, configurationKind: 'SAVED', contextProfileId: 'profile-b' }, global: { stubs: { RouterLink: true, 'el-button': { props: ['loading'], template: '<button><slot/></button>' } } } })
  wrappers.push(wrapper); await flushPromises(); return wrapper
}
function button(wrapper: VueWrapper, name: string) { return wrapper.findAll('button').find(item => item.text() === name)! }

describe('scheduled configuration backups', () => {
  beforeEach(() => {
    vi.resetAllMocks(); api.getConfigBackupSchedules.mockResolvedValue([schedule])
    api.createConfigBackupSchedule.mockResolvedValue(schedule); api.setConfigBackupScheduleEnabled.mockResolvedValue({ ...schedule, enabled: false }); api.deleteConfigBackupSchedule.mockResolvedValue({ deleted: true })
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })
  it('reads the exact context without creating a backup or schedule', async () => {
    const wrapper = await render()
    expect(api.getConfigBackupSchedules).toHaveBeenCalledWith('profile-b')
    expect(wrapper.text()).toContain('1 台 · 保存配置'); expect(wrapper.text()).toContain('已完成')
    expect(api.createConfigBackupSchedule).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('停机错过的周期不逐次补跑')
  })
  it('creates using a snapshot of the selected profiles, category and cycle', async () => {
    const wrapper = await render(['profile-b', 'profile-c'])
    await wrapper.get('select[aria-label=备份周期]').setValue('360')
    await button(wrapper, '为所选设备创建计划').trigger('click'); await flushPromises()
    expect(api.createConfigBackupSchedule).toHaveBeenCalledWith({ profileIds: ['profile-b', 'profile-c'], configurationKind: 'SAVED', intervalMinutes: 360 })
    expect(api.getConfigBackupSchedules).toHaveBeenCalledTimes(2)
  })
  it.each([{ ids: [] }, { ids: Array.from({ length: 33 }, (_, index) => `profile-${index}`) }])('blocks invalid selection count', async ({ ids }) => {
    const wrapper = await render(ids)
    expect(button(wrapper, '为所选设备创建计划').attributes('disabled')).toBeDefined()
    await button(wrapper, '为所选设备创建计划').trigger('click')
    expect(api.createConfigBackupSchedule).not.toHaveBeenCalled()
  })
  it('pauses and deletes an exact schedule, reloading the persisted list each time', async () => {
    const wrapper = await render()
    api.getConfigBackupSchedules.mockResolvedValueOnce([{ ...schedule, enabled: false }])
    await button(wrapper, '暂停').trigger('click'); await flushPromises()
    expect(api.setConfigBackupScheduleEnabled).toHaveBeenCalledWith('schedule-b', false)
    expect(wrapper.text()).toContain('已暂停'); expect(button(wrapper, '启用').exists()).toBe(true)
    api.getConfigBackupSchedules.mockResolvedValueOnce([])
    await button(wrapper, '删除').trigger('click'); await flushPromises()
    expect(api.deleteConfigBackupSchedule).toHaveBeenCalledWith('schedule-b')
    expect(wrapper.text()).toContain('暂无定时备份计划')
  })
  it('disables overlapping submissions while creation is pending', async () => {
    const pending = deferred<ConfigBackupSchedule>(); api.createConfigBackupSchedule.mockReturnValue(pending.promise)
    const wrapper = await render()
    await button(wrapper, '为所选设备创建计划').trigger('click')
    await button(wrapper, '为所选设备创建计划').trigger('click')
    expect(api.createConfigBackupSchedule).toHaveBeenCalledTimes(1)
    expect(button(wrapper, '暂停').attributes('disabled')).toBeDefined()
    pending.resolve(schedule); await flushPromises()
    expect(button(wrapper, '暂停').attributes('disabled')).toBeUndefined()
  })
  it('ignores an old context response after moving to another device', async () => {
    const old = deferred<ConfigBackupSchedule[]>(); api.getConfigBackupSchedules.mockReturnValueOnce(old.promise)
    const wrapper = await render()
    api.getConfigBackupSchedules.mockResolvedValueOnce([])
    await wrapper.setProps({ contextProfileId: 'profile-c', selectedProfileIds: ['profile-c'] }); await flushPromises()
    old.resolve([schedule]); await flushPromises()
    expect(api.getConfigBackupSchedules).toHaveBeenLastCalledWith('profile-c')
    expect(wrapper.text()).toContain('暂无定时备份计划'); expect(wrapper.text()).not.toContain('已完成')
  })
  it('clears results on permission revocation and keeps raw failure details out of the page', async () => {
    const wrapper = await render()
    api.setConfigBackupScheduleEnabled.mockRejectedValue(new ApiRequestError(403, 'private-host-detail'))
    await button(wrapper, '暂停').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('当前权限不可用'); expect(wrapper.text()).not.toContain('private-host-detail')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1); expect(wrapper.text()).not.toContain('已完成')
  })
})
