import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import SmartConfigView from './SmartConfigView.vue'
import { useAuthStore } from '../stores/auth'

const api = vi.hoisted(() => ({ getSmartConfigurationTemplates: vi.fn(), previewSmartConfiguration: vi.fn() }))
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), error: vi.fn(), warning: vi.fn() } }))

const templates = [{ id: 'h3c-interface-description', name: 'H3C 接口描述', description: '描述', variables: [
  { name: 'interface', label: '接口名称', example: 'GigabitEthernet1/0/1', required: true },
  { name: 'description', label: '描述', example: '上联', required: true },
] }]
const wrappers: VueWrapper[] = []

async function render(role: 'ADMIN' | 'SUPERADMIN' = 'SUPERADMIN') {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role } as never
  const wrapper = mount(SmartConfigView, { global: { plugins: [pinia], stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['loading', 'disabled'], template: '<button :disabled="disabled"><slot/></button>' },
  } } })
  wrappers.push(wrapper); await flushPromises(); return wrapper
}

describe('smart configuration dry-run UI', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getSmartConfigurationTemplates.mockResolvedValue(templates)
    api.previewSmartConfiguration.mockResolvedValue({ previewId: 'preview-1', templateId: templates[0].id,
      templateName: templates[0].name, state: 'PREVIEWED', executionMode: 'DRY_RUN_ONLY', deviceWriteAllowed: false,
      writeAttempted: false, commands: [{ index: 0, command: 'system-view', risk: 'WRITE_PREVIEW_ONLY' }], errors: [], createdMs: 1 })
  })
  afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

  it('loads variables, submits preview and renders write prohibition', async () => {
    const wrapper = await render()
    await wrapper.get('input[placeholder="GigabitEthernet1/0/1"]').setValue('GigabitEthernet1/0/1')
    await wrapper.get('input[placeholder="上联"]').setValue('车站上联')
    await wrapper.findAll('button').find(button => button.text() === '生成命令预览')!.trigger('click')
    await flushPromises()
    expect(api.previewSmartConfiguration).toHaveBeenCalledWith({ templateId: templates[0].id, variables: { interface: 'GigabitEthernet1/0/1', description: '车站上联' }, siteId: undefined })
    expect(wrapper.text()).toContain('设备写入：禁止')
    expect(wrapper.text()).toContain('DRY_RUN_ONLY')
  })

  it('does not enable preview for a non-superadmin', async () => {
    const wrapper = await render('ADMIN')
    const button = wrapper.findAll('button').find(item => item.text() === '生成命令预览')!
    expect(button.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('仅 SUPERADMIN 可生成预览')
    expect(api.previewSmartConfiguration).not.toHaveBeenCalled()
  })
})
