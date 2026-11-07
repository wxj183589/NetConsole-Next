import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { KeepAlive, h } from 'vue'
import { createMemoryHistory, createRouter, RouterView, type LocationQueryRaw } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ElMessage } from 'element-plus'
import { ApiRequestError } from '../api/client'
import { useAuthStore } from '../stores/auth'
import ConfigBackupsView from './ConfigBackupsView.vue'

const api = vi.hoisted(() => ({
  getH3cProfiles: vi.fn(), getDeviceInventoryDetail: vi.fn(), getModuleStates: vi.fn(), getConfigBackups: vi.fn(),
  createConfigBackup: vi.fn(), getConfigBackupBatch: vi.fn(), getConfigBackupDiff: vi.fn(), downloadConfigBackup: vi.fn(),
  previewConfigRestore: vi.fn(), executeConfigRestore: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }))

const profileA = { id: 'profile-a', host: '192.0.2.10', username: 'reader-a' }
const profileB = { id: 'profile-b', host: '192.0.2.10', username: 'reader-b' }
function detail(id = 'device-b', profile = profileB, vendor = 'H3C', type = 'SWITCH') {
  return { item: { id, name: id === 'device-b' ? '交换机乙' : '交换机甲', managementIp: profile.host, vendor, deviceType: type, terminalProfileId: profile.id }, terminalProfileId: profile.id, terminalProfile: profile }
}
function snapshots(profileId: string) {
  return [2, 1].map(version => ({ id: `${profileId}-v${version}`, profileId, version, byteCount: 1024, sha256: 'fixture-sha256', capturedMs: 1000 }))
}
function kindSnapshots(profileId: string, configurationKind: 'RUNNING' | 'SAVED' | 'STARTUP_INFO') {
  return snapshots(profileId).map(item => ({ ...item, id: `${profileId}-${configurationKind}-v${item.version}`, configurationKind }))
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve } }
const wrappers: VueWrapper[] = []
async function render(query: LocationQueryRaw = { deviceId: 'device-b' }, role = 'SUPERADMIN', cached = false) {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role } as never
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/config-backups', component: ConfigBackupsView }, { path: '/elsewhere', component: { template: '<div>其他页面</div>' } },
  ] })
  await router.push({ path: '/config-backups', query }); await router.isReady()
  const component = cached ? { render: () => h(RouterView, null, { default: ({ Component }: { Component: object }) => h(KeepAlive, null, [h(Component)]) }) } : ConfigBackupsView
  const wrapper = mount(component, { global: { plugins: [pinia, router], stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    ConfigBackupSchedules: true,
    'el-button': { props: ['loading'], template: '<button :data-loading="loading"><slot/></button>' },
  } } })
  wrappers.push(wrapper); await flushPromises()
  return { wrapper, router }
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }

describe('device configuration backup context', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getModuleStates.mockResolvedValue([{ key: 'config-backups', enabled: true }])
    api.getH3cProfiles.mockResolvedValue([profileA, profileB])
    api.getDeviceInventoryDetail.mockImplementation((id: string) => Promise.resolve(detail(id, id === 'device-a' ? profileA : profileB)))
    api.getConfigBackups.mockImplementation((id: string) => Promise.resolve(snapshots(id)))
    api.createConfigBackup.mockResolvedValue({ id: 'batch-b', state: 'SUCCEEDED', succeeded: 1, total: 1 })
    api.getConfigBackupDiff.mockResolvedValue({ addedLines: 1, removedLines: 0, added: ['当前配置'], removed: [] })
    api.previewConfigRestore.mockResolvedValue({ id: 'restore-preview', snapshotId: 'profile-b-v1', targetProfileId: 'profile-b', targetDeviceId: 'device-b', targetSiteId: 'local', configurationKind: 'RUNNING', sourceSha256: 'sha', currentSnapshotId: 'profile-b-v2', addedLines: 2, removedLines: 1, changedLines: 1, compatibility: 'PROFILE_EXACT_H3C', confirmationToken: 'token', expiresMs: 2000, state: 'PREVIEW' })
    api.executeConfigRestore.mockResolvedValue({ previewId: 'restore-preview', state: 'DRY_RUN', detail: '未写入', completedMs: 2000 })
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

  it('uses the exact inventory UUID and linked profile even when two devices share an IP; opening only reads', async () => {
    const { wrapper } = await render()
    expect(api.getDeviceInventoryDetail).toHaveBeenCalledWith('device-b')
    expect(api.getConfigBackups).toHaveBeenCalledTimes(1)
    expect(api.getConfigBackups).toHaveBeenCalledWith('profile-b', 'RUNNING')
    expect(wrapper.text()).toContain('交换机乙 · 192.0.2.10')
    expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(1)
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(true)
    expect(wrapper.find('select[aria-label=备份设备]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('profile-b')
    expect(api.createConfigBackup).not.toHaveBeenCalled()
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenCalledTimes(1)
    expect(api.createConfigBackup).toHaveBeenCalledWith(['profile-b'], 'RUNNING')
  })

  it('preserves the standalone page when there is no device query', async () => {
    const { wrapper } = await render({})
    expect(api.getDeviceInventoryDetail).not.toHaveBeenCalled()
    expect(api.getConfigBackups).toHaveBeenCalledWith('profile-a', 'RUNNING')
    expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(2)
    expect(button(wrapper, '开始配置备份').attributes('disabled')).toBeDefined()
    await wrapper.get('select[aria-label=备份设备]').setValue('profile-b'); await flushPromises()
    expect(api.getConfigBackups).toHaveBeenLastCalledWith('profile-b', 'RUNNING')
  })

  it('views an authorized snapshot and clears its contents when the device changes', async () => {
    api.downloadConfigBackup.mockResolvedValue({ text: async () => 'sysname example\npassword <REDACTED>' })
    const { wrapper } = await render({})
    await wrapper.findAll('button').find(item => item.text() === '查看')!.trigger('click'); await flushPromises()
    expect(api.downloadConfigBackup).toHaveBeenCalledWith('profile-a-v2')
    expect(wrapper.get('[aria-label="配置内容"]').text()).toContain('password <REDACTED>')
    await wrapper.get('select[aria-label=备份设备]').setValue('profile-b'); await flushPromises()
    expect(wrapper.find('[aria-label="配置内容"]').exists()).toBe(false)
  })

  it('retries only failed profiles from a completed backup batch', async () => {
    api.createConfigBackup.mockResolvedValueOnce({ id: 'batch-one', state: 'PARTIAL', succeeded: 1, total: 2,
      items: [{ profileId: 'profile-a', state: 'SUCCEEDED' }, { profileId: 'profile-b', state: 'FAILED', error: 'FAILED' }] })
    const { wrapper } = await render({})
    for (const input of wrapper.findAll('input[type=checkbox]')) await input.setValue(true)
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    await button(wrapper, '重试失败设备').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenLastCalledWith(['profile-b'], 'RUNNING')
  })

  it('reads and starts the selected configuration category while retaining the exact device association', async () => {
    api.getConfigBackups.mockImplementation((id: string, kind: 'RUNNING' | 'SAVED' | 'STARTUP_INFO') => Promise.resolve(kindSnapshots(id, kind)))
    api.createConfigBackup.mockResolvedValue({ id: 'saved-batch', configurationKind: 'SAVED', state: 'SUCCEEDED', succeeded: 1, total: 1 })
    const { wrapper } = await render()
    await wrapper.get('select[aria-label=配置类别]').setValue('SAVED'); await flushPromises()
    expect(api.getConfigBackups).toHaveBeenLastCalledWith('profile-b', 'SAVED')
    expect(wrapper.text()).toContain('保存配置版本历史')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(api.createConfigBackup).not.toHaveBeenCalled()
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenCalledWith(['profile-b'], 'SAVED')
    expect(wrapper.text()).toContain('保存配置 · 批次 1/1 · SUCCEEDED')
  })

  it('starts startup body collection through the dedicated backend route', async () => {
    api.getConfigBackups.mockResolvedValue([])
    const { wrapper } = await render()
    await wrapper.get('select[aria-label=配置类别]').setValue('STARTUP_BODY'); await flushPromises()
    expect(wrapper.text()).toContain('启动配置正文版本历史')
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenCalledWith(['profile-b'], 'STARTUP_BODY')
  })

  it('previews a restore and keeps dry-run separate from device write execution', async () => {
    const { wrapper } = await render()
    const previewButtons = wrapper.findAll('button').filter(item => item.text() === '恢复预览')
    expect(previewButtons).toHaveLength(2)
    await previewButtons[1].trigger('click'); await flushPromises()
    expect(api.previewConfigRestore).toHaveBeenCalledWith({ snapshotId: 'profile-b-v1', targetProfileId: 'profile-b' })
    expect(wrapper.text()).toContain('配置恢复预览')
    await button(wrapper, '执行 dry-run').trigger('click'); await flushPromises()
    expect(api.executeConfigRestore).toHaveBeenCalledWith({ previewId: 'restore-preview', confirmationToken: 'token', apply: false })
    expect(wrapper.text()).toContain('DRY_RUN')
    expect(api.executeConfigRestore).not.toHaveBeenCalledWith(expect.objectContaining({ apply: true }))
  })

  it('does not show a delayed restore preview after switching devices', async () => {
    const pending = deferred<object>()
    api.previewConfigRestore.mockReturnValue(pending.promise)
    const { wrapper } = await render({})
    await wrapper.findAll('button').find(item => item.text() === '恢复预览')!.trigger('click')
    await wrapper.get('select[aria-label=备份设备]').setValue('profile-b'); await flushPromises()
    pending.resolve({ id: 'old-preview', snapshotId: 'profile-a-v2' }); await flushPromises()
    expect(wrapper.find('[aria-label="配置恢复预览"]').exists()).toBe(false)
  })

  it('surfaces a blocked writer without claiming restore success', async () => {
    api.executeConfigRestore.mockRejectedValue(new ApiRequestError(409, 'CONFIG_RESTORE_WRITE_ADAPTER_UNAVAILABLE'))
    const { wrapper } = await render()
    await wrapper.findAll('button').find(item => item.text() === '恢复预览')!.trigger('click'); await flushPromises()
    await button(wrapper, '执行恢复').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('设备未被修改')
    expect(wrapper.text()).toContain('BLOCKED')
    expect(ElMessage.success).not.toHaveBeenCalledWith('配置恢复完成，请重新采集验证')
  })

  it('does not collapse a restore conflict into the disabled-writer message', async () => {
    api.executeConfigRestore.mockRejectedValue(new ApiRequestError(409, 'CONFIG_RESTORE_CURRENT_CHANGED: 目标当前配置已变化'))
    const { wrapper } = await render()
    await wrapper.findAll('button').find(item => item.text() === '恢复预览')!.trigger('click'); await flushPromises()
    await button(wrapper, '执行恢复').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('CONFIG_RESTORE_CURRENT_CHANGED')
    expect(wrapper.text()).not.toContain('写入适配器未启用')
  })

  it('filters mixed category history and only compares distinct versions of the selected category', async () => {
    api.getConfigBackups.mockResolvedValue([
      ...kindSnapshots('profile-b', 'RUNNING'),
      ...kindSnapshots('profile-b', 'SAVED'),
      ...kindSnapshots('profile-b', 'STARTUP_INFO'),
    ])
    const { wrapper } = await render()
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[0].text())).toEqual(['运行配置', '运行配置'])
    await wrapper.get('select[aria-label=配置类别]').setValue('SAVED'); await flushPromises()
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[0].text())).toEqual(['保存配置', '保存配置'])
    expect(wrapper.get('select[aria-label=比较基线]').findAll('option').map(option => option.attributes('value')))
      .toEqual(['profile-b-SAVED-v2', 'profile-b-SAVED-v1'])
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    expect(api.getConfigBackupDiff).toHaveBeenCalledWith('profile-b-SAVED-v1', 'profile-b-SAVED-v2')
    await wrapper.get('select[aria-label=比较基线]').setValue('profile-b-SAVED-v2')
    expect(button(wrapper, '比较').attributes('disabled')).toBeDefined()
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    expect(api.getConfigBackupDiff).toHaveBeenCalledTimes(1)
  })

  it('ignores a pending running-configuration history after switching to saved configuration', async () => {
    const old = deferred<ReturnType<typeof kindSnapshots>>()
    api.getConfigBackups.mockImplementation((id: string, kind: 'RUNNING' | 'SAVED' | 'STARTUP_INFO') =>
      kind === 'RUNNING' ? old.promise : Promise.resolve(kindSnapshots(id, kind)))
    const { wrapper } = await render()
    const selector = wrapper.get('select[aria-label=配置类别]')
    expect(selector.attributes('disabled')).toBeUndefined()
    await selector.setValue('SAVED'); await flushPromises()
    expect(api.getConfigBackups).toHaveBeenLastCalledWith('profile-b', 'SAVED')
    old.resolve(kindSnapshots('profile-b', 'RUNNING').map(item => ({ ...item, version: 99 }))); await flushPromises()
    expect(wrapper.text()).toContain('保存配置版本历史')
    expect(wrapper.text()).not.toContain('v99')
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[0].text())).toEqual(['保存配置', '保存配置'])
  })

  it('keeps a later backup busy when the initial history request completes', async () => {
    const history = deferred<ReturnType<typeof snapshots>>()
    const backup = deferred<object>()
    api.getConfigBackups.mockReturnValueOnce(history.promise)
    api.createConfigBackup.mockReturnValue(backup.promise)
    const { wrapper } = await render()
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenCalledWith(['profile-b'], 'RUNNING')
    history.resolve(snapshots('profile-b')); await flushPromises()
    expect(button(wrapper, '开始配置备份').attributes('data-loading')).toBe('true')
    expect(wrapper.get('select[aria-label=配置类别]').attributes('disabled')).toBeDefined()
    backup.resolve({ id: 'later-batch', configurationKind: 'RUNNING', state: 'SUCCEEDED', succeeded: 1, total: 1 }); await flushPromises()
    expect(button(wrapper, '开始配置备份').attributes('data-loading')).toBe('false')
  })

  it('clears an old difference and ignores its response after the configuration category changes', async () => {
    const old = deferred<object>()
    api.getConfigBackups.mockImplementation((id: string, kind: 'RUNNING' | 'SAVED' | 'STARTUP_INFO') => Promise.resolve(kindSnapshots(id, kind)))
    api.getConfigBackupDiff.mockReturnValue(old.promise)
    const { wrapper } = await render()
    await button(wrapper, '比较').trigger('click')
    await wrapper.get('select[aria-label=配置类别]').setValue('SAVED'); await flushPromises()
    old.resolve({ addedLines: 1, removedLines: 0, added: ['旧运行配置差异'], removed: [] }); await flushPromises()
    expect(wrapper.text()).not.toContain('旧运行配置差异')
    expect(wrapper.text()).toContain('保存配置版本差异')
  })

  it('describes startup information as file pointer metadata and explicitly collects that category', async () => {
    api.getConfigBackups.mockImplementation((id: string, kind: 'RUNNING' | 'SAVED' | 'STARTUP_INFO') => Promise.resolve(kindSnapshots(id, kind)))
    const { wrapper } = await render()
    await wrapper.get('select[aria-label=配置类别]').setValue('STARTUP_INFO'); await flushPromises()
    expect(api.getConfigBackups).toHaveBeenLastCalledWith('profile-b', 'STARTUP_INFO')
    expect(wrapper.text()).toContain('启动配置指向仅记录设备报告的启动文件信息，不包含文件正文，也不表示可以恢复配置。')
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[0].text())).toEqual(['启动配置指向', '启动配置指向'])
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(api.createConfigBackup).toHaveBeenCalledWith(['profile-b'], 'STARTUP_INFO')
  })

  it('explains an unavailable saved configuration without displaying a successful backup', async () => {
    api.getConfigBackups.mockResolvedValue([])
    api.createConfigBackup.mockResolvedValue({ id: 'missing-saved', configurationKind: 'SAVED', state: 'PARTIAL', succeeded: 0, total: 1,
      items: [{ profileId: 'profile-b', state: 'FAILED', error: 'CONFIG_BACKUP_SAVED_UNAVAILABLE' }] })
    const { wrapper } = await render()
    await wrapper.get('select[aria-label=配置类别]').setValue('SAVED'); await flushPromises()
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('设备没有可读取的保存配置，未生成备份。')
    expect(wrapper.text()).toContain('暂无保存配置记录')
    expect(ElMessage.success).not.toHaveBeenCalledWith('配置备份完成')
  })

  it.each([null, '', ['device-a', 'device-b']])('blocks malformed deviceId %j without falling back', async deviceId => {
    const { wrapper } = await render({ deviceId })
    expect(wrapper.text()).toContain('设备链接无效')
    expect(api.getDeviceInventoryDetail).not.toHaveBeenCalled()
    expect(api.getConfigBackups).not.toHaveBeenCalled()
    expect(wrapper.find('input[type=checkbox]').exists()).toBe(false)
  })

  it.each([403, 404])('blocks inaccessible inventory (%s) without loading the first profile', async status => {
    api.getDeviceInventoryDetail.mockRejectedValue(new ApiRequestError(status, 'unavailable'))
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('设备不存在或不在当前授权范围内')
    expect(api.getConfigBackups).not.toHaveBeenCalled()
    expect(api.createConfigBackup).not.toHaveBeenCalled()
  })

  it.each([['Huawei', 'SWITCH'], ['H3C', 'FIT_AP']])('blocks unsupported %s / %s devices', async (vendor, type) => {
    api.getDeviceInventoryDetail.mockResolvedValue(detail('device-b', profileB, vendor, type))
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('当前仅支持独立管理的 H3C')
    expect(api.getConfigBackups).not.toHaveBeenCalled()
  })

  it.each(['missing', 'conflicting', 'hidden', 'wrong-device'])('rejects %s associations without guessing', async mode => {
    const value = detail()
    if (mode === 'missing') value.terminalProfileId = ''
    if (mode === 'conflicting') value.item.terminalProfileId = profileA.id
    if (mode === 'hidden') value.terminalProfile = null as never
    if (mode === 'wrong-device') value.item.id = 'device-a'
    api.getDeviceInventoryDetail.mockResolvedValue(value)
    const { wrapper } = await render()
    expect(wrapper.find('input[type=checkbox]').exists()).toBe(false)
    expect(api.getConfigBackups).not.toHaveBeenCalled()
  })

  it('requires the exact associated profile to be in the authorized profile list', async () => {
    api.getH3cProfiles.mockResolvedValue([profileA])
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('连接资料不可用或不在当前授权范围内')
    expect(api.getConfigBackups).not.toHaveBeenCalled()
  })

  it('enforces the persisted module gate before reading device/history data', async () => {
    api.getModuleStates.mockResolvedValue([{ key: 'config-backups', enabled: false }])
    const { wrapper } = await render()
    expect(wrapper.text()).toContain('配置备份模块未启用')
    expect(api.getDeviceInventoryDetail).not.toHaveBeenCalled()
    expect(api.getH3cProfiles).not.toHaveBeenCalled()
    expect(api.getConfigBackups).not.toHaveBeenCalled()
  })

  it('lets an ADMIN read authorized history and differences while preventing backup execution', async () => {
    const { wrapper } = await render({ deviceId: 'device-b' }, 'ADMIN')
    expect(wrapper.text()).toContain('当前角色不能执行配置备份')
    expect(button(wrapper, '开始配置备份').attributes('disabled')).toBeDefined()
    await button(wrapper, '开始配置备份').trigger('click')
    expect(api.createConfigBackup).not.toHaveBeenCalled()
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    expect(api.getConfigBackupDiff).toHaveBeenCalledWith('profile-b-v1', 'profile-b-v2')
    expect(wrapper.text()).toContain('当前配置')
  })

  it('ignores a late device-detail response after the query changes', async () => {
    const old = deferred<ReturnType<typeof detail>>()
    api.getDeviceInventoryDetail.mockImplementation((id: string) => id === 'device-a' ? old.promise : Promise.resolve(detail()))
    const { wrapper, router } = await render({ deviceId: 'device-a' })
    await router.replace({ query: { deviceId: 'device-b' } }); await flushPromises()
    old.resolve(detail('device-a', profileA)); await flushPromises()
    expect(wrapper.text()).toContain('交换机乙')
    expect(wrapper.text()).not.toContain('交换机甲')
    expect(api.getConfigBackups).toHaveBeenCalledTimes(1)
    expect(api.getConfigBackups).toHaveBeenCalledWith('profile-b', 'RUNNING')
  })

  it('clears target history immediately and ignores late history responses', async () => {
    const old = deferred<ReturnType<typeof snapshots>>()
    api.getConfigBackups.mockImplementation((id: string) => id === 'profile-a' ? old.promise : Promise.resolve(snapshots(id)))
    const { wrapper, router } = await render({ deviceId: 'device-a' })
    await router.replace({ query: { deviceId: 'device-b' } }); await flushPromises()
    old.resolve([{ ...snapshots('profile-a')[0], version: 99 }]); await flushPromises()
    expect(wrapper.text()).toContain('交换机乙')
    expect(wrapper.text()).not.toContain('v99')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
  })

  it('clears prior differences and ignores a late comparison after the device changes', async () => {
    const old = deferred<object>()
    api.getConfigBackupDiff.mockReturnValue(old.promise)
    const { wrapper, router } = await render()
    await button(wrapper, '比较').trigger('click')
    await router.replace({ query: { deviceId: 'device-a' } }); await flushPromises()
    old.resolve({ addedLines: 1, removedLines: 0, added: ['旧设备配置'], removed: [] }); await flushPromises()
    expect(wrapper.text()).not.toContain('旧设备配置')
    expect(wrapper.text()).toContain('交换机甲')
  })

  it('does not display or poll an old backup batch after switching targets', async () => {
    const old = deferred<object>()
    api.createConfigBackup.mockReturnValue(old.promise)
    const { wrapper, router } = await render()
    await button(wrapper, '开始配置备份').trigger('click')
    await router.replace({ query: { deviceId: 'device-a' } }); await flushPromises()
    old.resolve({ id: 'old-batch', state: 'RUNNING', succeeded: 0, total: 1 }); await flushPromises()
    expect(wrapper.text()).not.toContain('批次')
    expect(api.getConfigBackupBatch).not.toHaveBeenCalled()
    expect(button(wrapper, '开始配置备份').attributes('disabled')).toBeUndefined()
  })

  it('removes cached data and execution controls when scope is revoked on refresh', async () => {
    const { wrapper } = await render()
    api.getDeviceInventoryDetail.mockRejectedValue(new ApiRequestError(403, 'revoked'))
    await button(wrapper, '刷新').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('设备不存在或不在当前授权范围内')
    expect(wrapper.text()).not.toContain('交换机乙')
    expect(wrapper.find('tbody').exists()).toBe(false)
    expect(button(wrapper, '开始配置备份')).toBeUndefined()
  })

  it('revalidates the module when a cached workbench is reactivated', async () => {
    const { wrapper, router } = await render({ deviceId: 'device-b' }, 'SUPERADMIN', true)
    await router.push('/elsewhere'); await flushPromises()
    api.getModuleStates.mockResolvedValue([{ key: 'config-backups', enabled: false }])
    await router.push({ path: '/config-backups', query: { deviceId: 'device-b' } }); await flushPromises()
    expect(wrapper.text()).toContain('配置备份模块未启用')
    expect(wrapper.text()).not.toContain('交换机乙')
  })

  it('shows the backend unsupported-vendor explanation and clears stale execution controls', async () => {
    api.createConfigBackup.mockRejectedValue(new ApiRequestError(409, 'CONFIG_BACKUP_TARGET_UNSUPPORTED: 当前配置备份仅支持 H3C 设备'))
    const { wrapper } = await render()
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('当前配置备份仅支持 H3C 设备')
    expect(wrapper.text()).not.toContain('CONFIG_BACKUP_TARGET_UNSUPPORTED')
    expect(button(wrapper, '开始配置备份')).toBeUndefined()
  })

  it('removes cached data when a comparison discovers scope revocation', async () => {
    api.getConfigBackupDiff.mockRejectedValue(new ApiRequestError(403, 'revoked'))
    const { wrapper } = await render()
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('设备不存在或不在当前授权范围内')
    expect(wrapper.find('tbody').exists()).toBe(false)
    expect(button(wrapper, '开始配置备份')).toBeUndefined()
  })

  it('does not download an old response after switching devices', async () => {
    const old = deferred<Blob>()
    api.downloadConfigBackup.mockReturnValue(old.promise)
    const createUrl = vi.spyOn(URL, 'createObjectURL')
    try {
      const { wrapper, router } = await render()
      await button(wrapper, '下载').trigger('click')
      await router.replace({ query: { deviceId: 'device-a' } }); await flushPromises()
      old.resolve(new Blob(['old configuration'])); await flushPromises()
      expect(createUrl).not.toHaveBeenCalled()
    } finally { createUrl.mockRestore() }
  })

  it('keeps a running task visible and does not report completion when the polling window expires', async () => {
    const running = { id: 'running-batch', state: 'RUNNING', succeeded: 0, total: 1 }
    api.createConfigBackup.mockResolvedValue(running)
    api.getConfigBackupBatch.mockResolvedValue(running)
    const { wrapper } = await render()
    vi.useFakeTimers()
    try {
      await button(wrapper, '开始配置备份').trigger('click')
      await vi.advanceTimersByTimeAsync(300000)
      expect(api.getConfigBackupBatch).toHaveBeenCalledTimes(1200)
      expect(ElMessage.info).toHaveBeenCalledWith('配置备份仍在运行，可稍后刷新')
      expect(ElMessage.success).not.toHaveBeenCalled()
      expect(wrapper.text()).toContain('RUNNING')
    } finally { vi.useRealTimers() }
  })

  it('unlocks refresh if authorization is revoked while reading history after backup', async () => {
    const { wrapper } = await render()
    api.getConfigBackups.mockRejectedValue(new ApiRequestError(403, 'revoked'))
    await button(wrapper, '开始配置备份').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('设备不存在或不在当前授权范围内')
    expect(button(wrapper, '刷新').attributes('data-loading')).toBe('false')
    expect(button(wrapper, '开始配置备份')).toBeUndefined()
    expect(ElMessage.success).not.toHaveBeenCalled()
  })

  it('renders every repeated diff line on both sides and preserves occurrences when comparing again', async () => {
    api.getConfigBackupDiff.mockResolvedValue({ addedLines: 3, removedLines: 2, added: ['重复配置', '重复配置', '新增配置'], removed: ['重复配置', '重复配置'] })
    const warning = vi.spyOn(console, 'warn')
    try {
      const { wrapper } = await render()
      await button(wrapper, '比较').trigger('click'); await flushPromises()
      const panels = wrapper.findAll('.diff-panel')
      expect(panels[0].findAll('pre').map(line => line.text())).toEqual(['+ 重复配置', '+ 重复配置', '+ 新增配置'])
      expect(panels[1].findAll('pre').map(line => line.text())).toEqual(['- 重复配置', '- 重复配置'])
      api.getConfigBackupDiff.mockResolvedValue({ addedLines: 2, removedLines: 3, added: ['重复配置', '重复配置'], removed: ['删除配置', '重复配置', '重复配置'] })
      await button(wrapper, '比较').trigger('click'); await flushPromises()
      const nextPanels = wrapper.findAll('.diff-panel')
      expect(nextPanels[0].findAll('pre').map(line => line.text())).toEqual(['+ 重复配置', '+ 重复配置'])
      expect(nextPanels[1].findAll('pre').map(line => line.text())).toEqual(['- 删除配置', '- 重复配置', '- 重复配置'])
      expect(warning.mock.calls.flat().join(' ')).not.toContain('Duplicate keys')
    } finally { warning.mockRestore() }
  })

  it('shows a preview limit notice only on the truncated side and keeps the total count visible', async () => {
    api.getConfigBackupDiff.mockResolvedValue({ addedLines: 300, removedLines: 2, added: Array(256).fill('重复新增配置'), removed: ['删除配置', '删除配置'] })
    const { wrapper } = await render()
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    const panels = wrapper.findAll('.diff-panel')
    expect(panels[0].get('h3').text()).toBe('新增 300 行')
    expect(panels[0].text()).toContain('已显示前256行，以上为差异总行数')
    expect(panels[0].findAll('pre')).toHaveLength(256)
    expect(panels[1].text()).not.toContain('已显示前')
  })

  it('does not show a truncation notice when all 256 lines are included', async () => {
    api.getConfigBackupDiff.mockResolvedValue({ addedLines: 256, removedLines: 0, added: Array(256).fill('重复配置'), removed: [] })
    const { wrapper } = await render()
    await button(wrapper, '比较').trigger('click'); await flushPromises()
    expect(wrapper.findAll('.diff-panel pre')).toHaveLength(256)
    expect(wrapper.text()).not.toContain('已显示前')
  })
})
