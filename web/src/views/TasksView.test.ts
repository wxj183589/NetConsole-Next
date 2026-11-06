import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import TasksView from './TasksView.vue'

const api = vi.hoisted(() => ({
  cancelTask: vi.fn(), createH3cTaskDefinition: vi.fn(), getH3cProfiles: vi.fn(), getTaskCommands: vi.fn(),
  getTasks: vi.fn(), resumeTask: vi.fn(), retryTask: vi.fn(), startH3cTaskDefinition: vi.fn(),
}))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }))
enableAutoUnmount(afterEach)

function render(role = 'SUPERADMIN') {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role } as never
  return mount(TasksView, { global: { plugins: [pinia], directives: { loading: () => undefined }, stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['disabled'], template: '<button :disabled="disabled"><slot/></button>' },
    'el-dialog': true,
  } } })
}
function button(wrapper: VueWrapper, label: string) { return wrapper.findAll('button').find(item => item.text() === label)! }

describe('H3C VLAN readonly task selection', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getTasks.mockResolvedValue([])
    api.getH3cProfiles.mockResolvedValue([{ id: 'profile-one', host: '192.0.2.10', port: 22, username: 'reader' }])
    api.createH3cTaskDefinition.mockResolvedValue(undefined)
    api.startH3cTaskDefinition.mockResolvedValue(undefined)
  })

  it('requires explicit submission and sends the fixed VLAN command through the existing task definition', async () => {
    const wrapper = render(); await flushPromises()
    expect(api.createH3cTaskDefinition).not.toHaveBeenCalled()
    expect(api.startH3cTaskDefinition).not.toHaveBeenCalled()
    expect(wrapper.text()).not.toContain('Credential Reference')
    expect(wrapper.text()).toContain('自动使用设备已保存的用户名和密码')
    const version = wrapper.get('input[type=checkbox][value="display version"]')
    const vlan = wrapper.get('input[type=checkbox][value="display vlan all"]')
    expect((vlan.element as HTMLInputElement).checked).toBe(false)
    await version.setValue(false)
    await vlan.setValue(true)
    await button(wrapper, '创建并执行计划').trigger('click'); await flushPromises()
    expect(api.createH3cTaskDefinition).toHaveBeenCalledTimes(1)
    expect(api.createH3cTaskDefinition).toHaveBeenCalledWith(expect.objectContaining({
      profileId: 'profile-one', command: 'display vlan all', commands: ['display vlan all'], timeoutMs: 30000, enabled: true,
    }))
    const definition = api.createH3cTaskDefinition.mock.calls[0][0]
    expect(api.startH3cTaskDefinition).toHaveBeenCalledWith(definition.id)
    expect(definition).not.toHaveProperty('password')
    expect(definition).not.toHaveProperty('credentialReference')
  })

  it('retains existing role restrictions for creating VLAN collection plans', async () => {
    const wrapper = render('ADMIN'); await flushPromises()
    expect(wrapper.find('input[value="display vlan all"]').exists()).toBe(false)
    expect(button(wrapper, '创建并执行计划')).toBeUndefined()
    expect(api.createH3cTaskDefinition).not.toHaveBeenCalled()
    expect(api.startH3cTaskDefinition).not.toHaveBeenCalled()
  })

  it('does not start a task when definition creation fails', async () => {
    api.createH3cTaskDefinition.mockRejectedValue(new Error('synthetic readonly policy rejection'))
    const wrapper = render(); await flushPromises()
    await wrapper.get('input[value="display version"]').setValue(false)
    await wrapper.get('input[value="display vlan all"]').setValue(true)
    await button(wrapper, '创建并执行计划').trigger('click'); await flushPromises()
    expect(api.startH3cTaskDefinition).not.toHaveBeenCalled()
  })
})
