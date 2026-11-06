import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import ZteView from './ZteView.vue'

const api = vi.hoisted(() => ({
  getZteProfiles: vi.fn(), getZteFacts: vi.fn(), getDeviceSites: vi.fn(), createZteProfile: vi.fn(), updateZteCredential: vi.fn(),
  getZteRun: vi.fn(), startZteCollection: vi.fn(),
}))
vi.mock('../api/client', () => api)
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

const profile = { id: '0123456789abcdef0123456789abcdef', host: '192.0.2.10', port: 22, username: 'reader', enabled: true, createdMs: 1, updatedMs: 1, terminalEncoding: 'AUTO', siteId: 'site-a', credentialConfigured: true }
function render(role = 'SUPERADMIN') {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'tester', username: 'tester', role } as never
  return mount(ZteView, { global: { plugins: [pinia], stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['nativeType'], template: '<button :type="nativeType || \'button\'"><slot/></button>' },
    'el-dialog': { props: ['modelValue'], template: '<section v-if="modelValue" role="dialog"><slot/><slot name="footer"/></section>' },
  } } })
}
function button(wrapper: ReturnType<typeof render>, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }

describe('ZTE device credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.getZteProfiles.mockResolvedValue([profile])
    api.getZteFacts.mockResolvedValue([])
    api.getDeviceSites.mockResolvedValue([{ id: 'site-a', name: 'Site A' }])
    api.createZteProfile.mockResolvedValue(profile)
    api.updateZteCredential.mockResolvedValue(undefined)
  })

  it('creates a scoped SSH profile with its password and removes the secret from the dialog after saving', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '新增 ZTE 设备').trigger('click')
    await flushPromises()
    const inputs = wrapper.findAll('[role=dialog] input')
    await inputs[0].setValue('192.0.2.10')
    await wrapper.get('input[autocomplete=username]').setValue('reader')
    await wrapper.get('input[type=password]').setValue('synthetic-create-secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.createZteProfile).toHaveBeenCalledWith(expect.objectContaining({ host: '192.0.2.10', username: 'reader', password: 'synthetic-create-secret', siteId: 'site-a', hostKeyUpdatePolicy: 'AUTO_UPDATE' }))
    expect(api.createZteProfile.mock.calls[0][0].id).toMatch(/^[a-f0-9]{32}$/)
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
    expect(wrapper.html()).not.toContain('synthetic-create-secret')
  })

  it('keeps an existing credential on a blank save and clears it only through the explicit control', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '更新密码').trigger('click')
    await button(wrapper, '保存密码').trigger('click')
    await flushPromises()
    expect(api.updateZteCredential).toHaveBeenLastCalledWith(profile.id, { password: undefined, clearPassword: false })
    await button(wrapper, '更新密码').trigger('click')
    await wrapper.get('input[type=password]').setValue('discard-when-clearing')
    await wrapper.get('input[type=checkbox]').setValue(true)
    expect(wrapper.get('input[type=password]').attributes('disabled')).toBeDefined()
    await button(wrapper, '保存密码').trigger('click')
    await flushPromises()
    expect(api.updateZteCredential).toHaveBeenLastCalledWith(profile.id, { password: undefined, clearPassword: true })
  })

  it('sends replacement passwords only to the credential operation and hides mutation actions for other roles', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '更新密码').trigger('click')
    await wrapper.get('input[type=password]').setValue('synthetic-replacement-secret')
    await button(wrapper, '保存密码').trigger('click')
    await flushPromises()
    expect(api.updateZteCredential).toHaveBeenCalledWith(profile.id, { password: 'synthetic-replacement-secret', clearPassword: false })
    expect(api.createZteProfile).not.toHaveBeenCalled()
    expect(wrapper.html()).not.toContain('synthetic-replacement-secret')
    const reader = render('ADMIN')
    await flushPromises()
    expect(reader.text()).not.toContain('新增 ZTE 设备')
    expect(reader.text()).not.toContain('更新密码')
    expect(button(reader, '采集')).toBeUndefined()
  })
})
