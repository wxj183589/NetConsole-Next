import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import LinuxServersView from './LinuxServersView.vue'

const api = vi.hoisted(() => ({ getLinuxServers: vi.fn(), getDeviceSites: vi.fn(), createLinuxServer: vi.fn(), updateLinuxServer: vi.fn(), deleteLinuxServer: vi.fn(), testLinuxServer: vi.fn() }))
const push = vi.hoisted(() => vi.fn())
vi.mock('../api/client', () => api)
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))
const server = { id: 'server-one', name: 'Operations server', host: '192.0.2.10', port: 22, username: 'operator', groupName: 'Operations', siteId: 'site-a', enabled: true, credentialConfigured: true, hostKeySha256: '', lastResult: 'CONNECTED' }

function render(role = 'SUPERADMIN') {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role } as never
  return mount(LinuxServersView, { global: { plugins: [pinia], stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['nativeType'], template: '<button :type="nativeType || \'button\'"><slot/></button>' },
    'el-dialog': { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot/><slot name="footer"/></section>' },
  } } })
}
function button(wrapper: ReturnType<typeof render>, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }

describe('Linux server management', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.getLinuxServers.mockResolvedValue([server])
    api.getDeviceSites.mockResolvedValue([{ id: 'site-a', name: 'Site A' }])
    api.createLinuxServer.mockResolvedValue(server)
    api.updateLinuxServer.mockResolvedValue(server)
    api.deleteLinuxServer.mockResolvedValue(undefined)
    api.testLinuxServer.mockResolvedValue({ status: 'CONNECTED', message: 'SSH 连接成功' })
  })

  it('creates a site-scoped server and clears the password after save', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '添加服务器').trigger('click')
    await flushPromises()
    const inputs = wrapper.findAll('[role=dialog] input')
    await inputs[0].setValue('Operations server')
    await inputs[1].setValue('192.0.2.10')
    await wrapper.get('input[autocomplete=username]').setValue('operator')
    await wrapper.get('input[type=password]').setValue('synthetic-linux-secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.createLinuxServer).toHaveBeenCalledWith(expect.objectContaining({ name: 'Operations server', host: '192.0.2.10', username: 'operator', password: 'synthetic-linux-secret', siteId: 'site-a', port: 22 }))
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
    expect(wrapper.html()).not.toContain('synthetic-linux-secret')
  })

  it('preserves blank edit passwords and clears credentials only when selected', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '编辑').trigger('click')
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.updateLinuxServer).toHaveBeenLastCalledWith(server.id, expect.objectContaining({ password: undefined, clearPassword: false }))
    await button(wrapper, '编辑').trigger('click')
    await flushPromises()
    await wrapper.get('input[type=password]').setValue('discarded-secret')
    await wrapper.findAll('input[type=checkbox]')[1].setValue(true)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.updateLinuxServer).toHaveBeenLastCalledWith(server.id, expect.objectContaining({ password: undefined, clearPassword: true }))
  })

  it('filters servers, opens the Linux terminal route and displays test feedback', async () => {
    const wrapper = render()
    await flushPromises()
    await wrapper.get('input[aria-label="搜索服务器"]').setValue('missing')
    expect(wrapper.text()).toContain('没有符合筛选条件')
    await wrapper.get('input[aria-label="搜索服务器"]').setValue('192.0.2.10')
    await button(wrapper, '打开终端').trigger('click')
    expect(push).toHaveBeenCalledWith({ path: '/terminal', query: { linuxServerId: server.id } })
    await button(wrapper, '测试连接').trigger('click')
    await flushPromises()
    expect(api.testLinuxServer).toHaveBeenCalledWith(server.id)
    expect(wrapper.get('[role=status]').text()).toContain('SSH 连接成功')
  })

  it('requires a concrete delete confirmation and hides mutations for other users', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '删除').trigger('click')
    expect(api.deleteLinuxServer).not.toHaveBeenCalled()
    expect(wrapper.get('[role=dialog]').text()).toContain('Operations server')
    await button(wrapper, '确认删除').trigger('click')
    await flushPromises()
    expect(api.deleteLinuxServer).toHaveBeenCalledWith(server.id)
    const scoped = render('ADMIN')
    await flushPromises()
    expect(button(scoped, '添加服务器')).toBeUndefined()
    expect(button(scoped, '编辑')).toBeUndefined()
    expect(button(scoped, '删除')).toBeUndefined()
    expect(button(scoped, '打开终端')).toBeDefined()
  })
})
