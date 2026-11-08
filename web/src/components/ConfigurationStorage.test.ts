import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ConfigurationStorage from './ConfigurationStorage.vue'

const api = vi.hoisted(() => ({ getConfigurationStorage: vi.fn() }))
vi.mock('../api/configurationStorage', () => api)
const fixture = { profileId: 'a', manifestCount: 2, maximumBackupsPerProfile: 2, profileStoredBytes: 2048,
  maximumStoredBytes: 4096, sharedStoredBytes: 4096, availableDiskBytes: 1048576,
  profileLimitReached: true, sharedLimitReached: true, measuredMs: 1000, automaticDeletion: false,
  retentionPolicy: 'PRESERVE_ALL_STOP_AT_CAPACITY' }
function render() { return mount(ConfigurationStorage, { props: { profileId: 'a' }, global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } }) }
beforeEach(() => { vi.resetAllMocks(); api.getConfigurationStorage.mockResolvedValue(fixture) })
it('reads capacity explicitly and explains retained history at quota', async () => {
  const wrapper = render()
  expect(api.getConfigurationStorage).not.toHaveBeenCalled()
  await wrapper.get('button').trigger('click'); await flushPromises()
  expect(api.getConfigurationStorage).toHaveBeenCalledWith('a')
  expect(wrapper.text()).toContain('清单 2 / 2')
  expect(wrapper.get('[role="alert"]').text()).toContain('现有历史仍可读取')
  wrapper.unmount()
})
it('does not invent shared occupancy for scoped authority', async () => {
  api.getConfigurationStorage.mockResolvedValue({ ...fixture, sharedStoredBytes: null, sharedLimitReached: null, profileLimitReached: false })
  const wrapper = render(); await wrapper.get('button').trigger('click'); await flushPromises()
  expect(wrapper.text()).toContain('当前权限不提供全局占用')
  expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  wrapper.unmount()
})
it('discards late results after changing device', async () => {
  let resolve!: (value: unknown) => void
  api.getConfigurationStorage.mockReturnValue(new Promise(done => { resolve = done }))
  const wrapper = render(); await wrapper.get('button').trigger('click')
  await wrapper.setProps({ profileId: 'b' }); resolve(fixture); await flushPromises()
  expect(wrapper.text()).not.toContain('清单 2')
  wrapper.unmount()
})
it('shows bounded retry advice on failed read or wrong profile', async () => {
  api.getConfigurationStorage.mockResolvedValue({ ...fixture, profileId: 'other' })
  const wrapper = render(); await wrapper.get('button').trigger('click'); await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('容量信息暂不可读')
  expect(wrapper.text()).not.toContain('清单 2')
  wrapper.unmount()
})
