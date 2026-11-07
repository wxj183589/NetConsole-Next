import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import SimulatorConfiguration from './SimulatorConfiguration.vue'

const api = vi.hoisted(() => ({ captureCompleteConfiguration: vi.fn(), changeSimulatorDescription: vi.fn(), getCompleteConfigurations: vi.fn(), getCompleteConfigurationView: vi.fn(), getSimulatorConfigurationChanges: vi.fn() }))
vi.mock('../api/client', () => api)
const backup = { id: 'complete-a', profileId: 'a', capturedMs: 1000, integrity: 'VERIFIED', restorable: true }
function render() { return mount(SimulatorConfiguration, { props: { profileId: 'a' }, global: { stubs: { 'el-button': { template: '<button><slot /></button>' } } } }) }
function button(wrapper: ReturnType<typeof render>, name: string) { return wrapper.findAll('button').find(item => item.text() === name)! }
describe('simulator configuration changes', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getCompleteConfigurations.mockResolvedValue([backup]); api.getSimulatorConfigurationChanges.mockResolvedValue([]); api.changeSimulatorDescription.mockResolvedValue({ state: 'VERIFIED' }) })
  it('requires a verified baseline and explicit confirmation before writing', async () => {
    const wrapper = render()
    expect(api.captureCompleteConfiguration).not.toHaveBeenCalled()
    expect(button(wrapper, '执行接口描述变更').attributes('disabled')).toBeDefined()
    await button(wrapper, '刷新完整备份').trigger('click'); await flushPromises()
    await wrapper.get('input[placeholder="GigabitEthernet1/0/1"]').setValue('GigabitEthernet1/0/1')
    await wrapper.get('input[maxlength="80"]').setValue('test-description')
    expect(button(wrapper, '执行接口描述变更').attributes('disabled')).toBeDefined()
    await wrapper.findAll('input[type="checkbox"]')[1].setValue(true)
    await button(wrapper, '执行接口描述变更').trigger('click'); await flushPromises()
    expect(api.changeSimulatorDescription).toHaveBeenCalledWith({ profileId: 'a', expectedBackupId: 'complete-a', interfaceName: 'GigabitEthernet1/0/1', description: 'test-description' })
    expect(button(wrapper, '执行接口描述变更').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('discards a previous device response and clears confirmation on context change', async () => {
    let resolve!: (value: unknown) => void
    api.getCompleteConfigurations.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = render()
    await button(wrapper, '刷新完整备份').trigger('click')
    await wrapper.setProps({ profileId: 'b' })
    resolve([backup]); await flushPromises()
    expect(wrapper.findAll('option')).toHaveLength(1)
    expect(button(wrapper, '执行接口描述变更').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('never selects an incomplete backup for a write baseline', async () => {
    api.getCompleteConfigurations.mockResolvedValue([{ ...backup, restorable: false }])
    const wrapper = render()
    await button(wrapper, '刷新完整备份').trigger('click'); await flushPromises()
    expect(wrapper.get('select').element.value).toBe('')
    expect(wrapper.findAll('option')[1].attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
})
