import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../stores/auth'
import SiteSshJumpHostPanel from './SiteSshJumpHostPanel.vue'

const api = vi.hoisted(() => ({
  getDeviceSites: vi.fn(), getSiteSshJumpHost: vi.fn(), saveSiteSshJumpHost: vi.fn(),
  testSiteSshJumpHost: vi.fn(), refreshSiteSshJumpHostFingerprint: vi.fn(), clearSiteSshJumpHostFingerprint: vi.fn(),
}))
vi.mock('../api/client', () => api)
const siteA = { siteId: 'site-a', host: '192.0.2.1', port: 2222, username: 'jump-a', enabled: true,
  credentialConfigured: true, hostKeySha256: 'SHA256:fixture-a', updatedMs: 1, runtimeStatus: 'ON_DEMAND' }
const siteB = { ...siteA, siteId: 'site-b', host: '192.0.2.2', username: 'jump-b', hostKeySha256: 'SHA256:fixture-b' }
function render(role = 'SUPERADMIN') {
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'tester', username: 'tester', role } as never
  return mount(SiteSshJumpHostPanel, { global: { plugins: [pinia], stubs: {
    'el-button': { props: ['nativeType'], template: '<button :type="nativeType || \'button\'"><slot/></button>' },
  } } })
}
function button(wrapper: ReturnType<typeof render>, text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

describe('Site SSH Jump Host settings', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.getDeviceSites.mockResolvedValue([{ id: 'site-a', name: 'Site A' }, { id: 'site-b', name: 'Site B' }])
    api.getSiteSshJumpHost.mockImplementation(async (id: string) => id === 'site-a' ? siteA : siteB)
    api.saveSiteSshJumpHost.mockResolvedValue(siteA)
    api.testSiteSshJumpHost.mockResolvedValue(siteA)
    api.refreshSiteSshJumpHostFingerprint.mockResolvedValue(siteA)
    api.clearSiteSshJumpHostFingerprint.mockResolvedValue({ ...siteA, hostKeySha256: '' })
  })

  it('retains a blank password and gives explicit clear precedence over typed input', async () => {
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).toContain('按需待命')
    expect(wrapper.text()).toContain('密码已保存')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.saveSiteSshJumpHost).toHaveBeenLastCalledWith('site-a', expect.objectContaining({ password: undefined, clearPassword: false }))
    await wrapper.get('input[type=password]').setValue('discard-when-clearing')
    await wrapper.findAll('input[type=checkbox]')[1].setValue(true)
    expect(wrapper.get('input[type=password]').attributes('disabled')).toBeDefined()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(api.saveSiteSshJumpHost).toHaveBeenLastCalledWith('site-a', expect.objectContaining({ password: undefined, clearPassword: true }))
    expect((wrapper.get('input[type=password]').element as HTMLInputElement).value).toBe('')
  })

  it('uses dedicated probe and fingerprint operations without removing saved credentials', async () => {
    const wrapper = render()
    await flushPromises()
    await button(wrapper, '测试连接').trigger('click')
    await flushPromises()
    expect(api.testSiteSshJumpHost).toHaveBeenCalledWith('site-a')
    await button(wrapper, '重新获取指纹').trigger('click')
    await flushPromises()
    expect(api.refreshSiteSshJumpHostFingerprint).toHaveBeenCalledWith('site-a')
    await button(wrapper, '删除指纹').trigger('click')
    await flushPromises()
    expect(api.clearSiteSshJumpHostFingerprint).toHaveBeenCalledWith('site-a')
    expect(wrapper.text()).toContain('未记录')
    expect(wrapper.text()).toContain('密码已保存')
    expect(api.saveSiteSshJumpHost).not.toHaveBeenCalled()
  })

  it('ignores a delayed read from the previously selected Site', async () => {
    const pending = deferred<typeof siteA>()
    api.getSiteSshJumpHost.mockImplementation((id: string) => id === 'site-a' ? pending.promise : Promise.resolve(siteB))
    const wrapper = render()
    await flushPromises()
    await wrapper.get('select').setValue('site-b')
    await flushPromises()
    pending.resolve(siteA)
    await flushPromises()
    expect(wrapper.text()).toContain('192.0.2.2:2222')
    expect(wrapper.text()).not.toContain('192.0.2.1:2222')
    expect(wrapper.get('input[autocomplete=username]').element).toHaveProperty('value', 'jump-b')
  })

  it('clears typed secrets on Site switch and isolates an in-flight save completion', async () => {
    const pending = deferred<typeof siteA>()
    api.saveSiteSshJumpHost.mockReturnValue(pending.promise)
    const wrapper = render()
    await flushPromises()
    await wrapper.get('input[type=password]').setValue('synthetic-site-a-secret')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('select').setValue('site-b')
    await flushPromises()
    expect((wrapper.get('input[type=password]').element as HTMLInputElement).value).toBe('')
    pending.resolve(siteA)
    await flushPromises()
    expect(wrapper.text()).toContain('192.0.2.2:2222')
    expect(wrapper.text()).not.toContain('设置已保存')
    expect(api.saveSiteSshJumpHost).toHaveBeenCalledWith('site-a', expect.objectContaining({ password: 'synthetic-site-a-secret' }))
  })

  it('shows missing credentials and disabled configuration without claiming an active tunnel', async () => {
    api.getSiteSshJumpHost.mockResolvedValue({ ...siteA, credentialConfigured: false, runtimeStatus: 'CREDENTIAL_MISSING' })
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).toContain('缺少密码')
    expect(button(wrapper, '测试连接').attributes('disabled')).toBeDefined()
    api.getSiteSshJumpHost.mockResolvedValue({ ...siteA, enabled: false, runtimeStatus: 'DISABLED' })
    await button(wrapper, '重新读取').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('已停用，后续 SSH 直连')
  })

  it('hides mutation controls from administrators', async () => {
    const wrapper = render('ADMIN')
    await flushPromises()
    expect(button(wrapper, '保存设置')).toBeUndefined()
    expect(button(wrapper, '测试连接')).toBeUndefined()
    expect(button(wrapper, '删除指纹')).toBeUndefined()
    expect(wrapper.get('input[autocomplete=username]').attributes('disabled')).toBeDefined()
  })
})
