import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { reactive } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import { useAuthStore } from '../stores/auth'
import TasksView from './TasksView.vue'
import { ApiRequestError, type TaskDiagnosticManifest } from '../api/client'

const api = vi.hoisted(() => ({
  cancelTask: vi.fn(), createH3cTaskDefinition: vi.fn(), getH3cProfiles: vi.fn(), getTaskCommands: vi.fn(),
  getTasks: vi.fn(), getTaskDiagnostics: vi.fn(), downloadTaskDiagnostics: vi.fn(), resumeTask: vi.fn(), retryTask: vi.fn(), startH3cTaskDefinition: vi.fn(),
}))
const route = reactive({ fullPath: '/tasks' })
vi.mock('../api/client', async original => ({ ...await original<object>(), ...api }))
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn(), success: vi.fn(), warning: vi.fn() } }))
vi.mock('vue-router', () => ({ useRoute: () => route }))
enableAutoUnmount(afterEach)

function render(role = 'SUPERADMIN') {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role } as never
  return mount(TasksView, { global: { plugins: [pinia], directives: { loading: () => undefined }, stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-button': { props: ['disabled'], template: '<button :disabled="disabled"><slot/></button>' },
    'el-dialog': { props: ['modelValue'], emits: ['update:modelValue'], template: '<section v-if="modelValue" role="dialog"><button @click="$emit(\'update:modelValue\', false)">关闭明细</button><slot/></section>' },
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
    expect(button(wrapper, '采集全机诊断')).toBeUndefined()
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

  it('selects only the three basic AC commands before explicit submission', async () => {
    const wrapper = render(); await flushPromises()
    await button(wrapper, '选择 AC 基础采集').trigger('click')
    const selected = wrapper.findAll('input[type=checkbox]').filter(item => (item.element as HTMLInputElement).checked)
    expect(selected.map(item => item.attributes('value'))).toEqual([
      'display wlan ap all', 'display wlan ap all address', 'display wlan ap all radio',
    ])
    expect(api.createH3cTaskDefinition).not.toHaveBeenCalled()
    await button(wrapper, '创建并执行计划').trigger('click'); await flushPromises()
    expect(api.createH3cTaskDefinition).toHaveBeenCalledWith(expect.objectContaining({
      commands: ['display wlan ap all', 'display wlan ap all address', 'display wlan ap all radio'], timeoutMs: 60000,
    }))
  })

  it('collects a standalone full diagnostic through saved credentials and the bounded task API', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('计划总超时')
    expect(wrapper.text()).toContain('最多 10 分钟')
    expect(wrapper.find('input[value="display diagnostic-information"]').exists()).toBe(false)
    expect(api.createH3cTaskDefinition).not.toHaveBeenCalled()
    await button(wrapper, '采集全机诊断').trigger('click'); await flushPromises()
    const definition = api.createH3cTaskDefinition.mock.calls[0][0]
    expect(definition).toEqual({ id: expect.any(String), profileId: 'profile-one',
      command: 'display diagnostic-information', commands: ['display diagnostic-information'], timeoutMs: 600000, enabled: true })
    expect(api.startH3cTaskDefinition).toHaveBeenCalledWith(definition.id)
    expect(JSON.stringify(definition)).not.toMatch(/password|credentialReference|save|screen-length/)
    expect((wrapper.get('input[value="display version"]').element as HTMLInputElement).checked).toBe(true)
  })

  it('does not start diagnostic collection after a rejected definition', async () => {
    api.createH3cTaskDefinition.mockRejectedValue(new Error('synthetic rejection'))
    const wrapper = render(); await flushPromises()
    await button(wrapper, '采集全机诊断').trigger('click'); await flushPromises()
    expect(api.startH3cTaskDefinition).not.toHaveBeenCalled()
  })
})

function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes }); return { promise, resolve } }
const task = { id: 'readonly-run', taskType: 'H3C_COMMAND', state: 'COMPLETED', captureStatus: 'CAPTURED', parseStatus: 'PARSED' }
const details = [
  { index: 0, command: 'display version', state: 'COMPLETED', captureStatus: 'CAPTURED', parseStatus: 'UNSUPPORTED', rawOutputPath: 'never-display-this-path' },
  { index: 1, command: 'display current-configuration', state: 'COMPLETED', captureStatus: 'CAPTURED', parseStatus: 'PARSED' },
  { index: 2, command: 'display vlan all', state: 'FAILED', captureStatus: 'FAILED', parseStatus: 'FAILED' },
]
function manifest(runId = task.id): TaskDiagnosticManifest { return { version: 1, runId, taskState: 'COMPLETED', status: 'PARTIAL', canDownload: true, commands: details.map(item => ({
  index: item.index, command: item.command, availability: item.index === 0 ? 'READY' : item.index === 1 ? 'CONFIG_EXCLUDED' : 'NO_RAW', canDownload: item.index === 0,
  captureStatus: item.captureStatus, parseStatus: item.parseStatus, capturedMs: 1_800_000_000_000, byteCount: item.index === 0 ? 12 : null, outputSha256: null, segmentSha256: null,
})) } }
async function openDetails(wrapper: VueWrapper, index = 0) { await wrapper.findAll('button').filter(item => item.text() === '命令明细')[index].trigger('click'); await flushPromises() }

describe('CLI diagnostic downloads', () => {
  let createUrl: MockInstance<typeof URL.createObjectURL>; let revokeUrl: MockInstance<typeof URL.revokeObjectURL>; let anchorClick: MockInstance<typeof HTMLAnchorElement.prototype.click>; let names: string[]
  beforeEach(() => {
    vi.resetAllMocks(); route.fullPath = '/tasks'; names = []
    api.getTasks.mockResolvedValue([task]); api.getH3cProfiles.mockResolvedValue([])
    api.getTaskCommands.mockResolvedValue(details); api.getTaskDiagnostics.mockImplementation(async (id: string) => manifest(id))
    api.downloadTaskDiagnostics.mockResolvedValue(new Blob(['diagnostic fixture']))
    if (!URL.createObjectURL) Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: () => '' })
    if (!URL.revokeObjectURL) Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: () => undefined })
    createUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:diagnostic-fixture')
    revokeUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined)
    anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { names.push(this.download) })
  })
  afterEach(() => { createUrl.mockRestore(); revokeUrl.mockRestore(); anchorClick.mockRestore() })

  it('uses manifest eligibility for individual downloads and excludes configuration despite successful capture', async () => {
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    expect(wrapper.findAll('button').filter(item => item.text() === '下载 TXT')).toHaveLength(1)
    expect(wrapper.text()).toContain('密码等敏感字段会脱敏'); expect(wrapper.text()).toContain('暂无输出')
    expect(wrapper.text()).not.toContain('never-display-this-path')
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(false)
    expect(api.downloadTaskDiagnostics).not.toHaveBeenCalled()
  })

  it('downloads TXT and ZIP with the correct command index and releases each ObjectURL', async () => {
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    expect(api.downloadTaskDiagnostics).toHaveBeenNthCalledWith(1, task.id, 0, expect.any(AbortSignal))
    await button(wrapper, '下载诊断 ZIP').trigger('click'); await flushPromises()
    expect(api.downloadTaskDiagnostics).toHaveBeenNthCalledWith(2, task.id, undefined, expect.any(AbortSignal))
    expect(names).toEqual(['diagnostics-readonly-run-command-001.txt', 'diagnostics-readonly-run.zip'])
    expect(api.getTaskDiagnostics).toHaveBeenCalledTimes(3)
    expect(createUrl).toHaveBeenCalledTimes(2); expect(revokeUrl).toHaveBeenCalledTimes(2)
    expect(document.querySelector('a[download]')).toBeNull()
  })

  it('disables the bundle when the manifest rejects it while retaining eligible individual commands', async () => {
    api.getTaskDiagnostics.mockResolvedValue({ ...manifest(), canDownload: false })
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
    expect(button(wrapper, '下载 TXT')).toBeDefined()
  })

  it('does not infer eligibility from a command capture when the manifest cannot be read', async () => {
    api.getTaskDiagnostics.mockRejectedValue(new Error('synthetic API failure'))
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    expect(wrapper.get('[role=alert]').text()).toBe('诊断输出读取失败，请重新打开明细。')
    expect(button(wrapper, '下载 TXT')).toBeUndefined()
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
  })

  it('clears command details when either read discovers revoked scope', async () => {
    api.getTaskCommands.mockRejectedValue(new ApiRequestError(403, 'denied'))
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    expect(wrapper.get('[role=alert]').text()).toContain('访问权限已变化')
    expect(wrapper.get('[role=dialog]').text()).not.toContain('display version')
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
  })

  it('rejects mismatched manifest identities', async () => {
    api.getTaskDiagnostics.mockResolvedValue(manifest('wrong-run'))
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    expect(button(wrapper, '下载 TXT')).toBeUndefined()
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
  })

  it('ignores command and manifest replies after switching tasks', async () => {
    const pending = deferred<TaskDiagnosticManifest>(); const oldDetails = deferred<typeof details>()
    api.getTasks.mockResolvedValue([task, { ...task, id: 'second-run' }])
    api.getTaskDiagnostics.mockImplementation((id: string) => id === task.id ? pending.promise : Promise.resolve({ ...manifest(id), canDownload: false, commands: [] }))
    api.getTaskCommands.mockImplementation((id: string) => id === task.id ? oldDetails.promise : Promise.resolve([]))
    const wrapper = render(); await flushPromises(); await openDetails(wrapper); await openDetails(wrapper, 1)
    oldDetails.resolve(details); pending.resolve(manifest()); await flushPromises()
    expect(wrapper.get('[role=dialog]').text()).not.toContain('display version')
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
    expect(api.getTaskCommands.mock.calls[0][1].aborted).toBe(true)
  })

  it.each(['close', 'task', 'route', 'auth', 'unmount'])('ignores a late Blob after %s and aborts its request', async change => {
    const pending = deferred<Blob>(); api.downloadTaskDiagnostics.mockReturnValue(pending.promise)
    api.getTasks.mockResolvedValue([task, { ...task, id: 'second-run' }])
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    await button(wrapper, '下载 TXT').trigger('click')
    const signal = api.downloadTaskDiagnostics.mock.calls[0][2] as AbortSignal
    if (change === 'close') await button(wrapper, '关闭明细').trigger('click')
    else if (change === 'task') await openDetails(wrapper, 1)
    else if (change === 'route') route.fullPath = '/tasks?siteId=other-site'
    else if (change === 'auth') useAuthStore().user = { username: 'tester', role: 'ADMIN' }
    else wrapper.unmount()
    pending.resolve(new Blob(['late response'])); await flushPromises()
    expect(createUrl).not.toHaveBeenCalled(); expect(signal.aborted).toBe(true)
  })

  it('revalidates download eligibility after receiving the Blob', async () => {
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    api.getTaskDiagnostics.mockResolvedValue({ ...manifest(), canDownload: false, commands: [] })
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    expect(createUrl).not.toHaveBeenCalled()
    expect(wrapper.get('[role=alert]').text()).toContain('可用性已变化')
    expect(button(wrapper, '下载 TXT')).toBeUndefined()
  })

  it('discards a received Blob if the final permission check is denied', async () => {
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    api.getTaskDiagnostics.mockRejectedValue(new ApiRequestError(404, 'RESOURCE_NOT_VISIBLE'))
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    expect(createUrl).not.toHaveBeenCalled()
    expect(wrapper.get('[role=alert]').text()).toContain('访问权限已变化')
    expect(wrapper.get('[role=dialog]').text()).not.toContain('display version')
  })

  it('ignores a late final manifest after closing the dialog', async () => {
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    const pending = deferred<TaskDiagnosticManifest>(); api.getTaskDiagnostics.mockReturnValue(pending.promise)
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    await button(wrapper, '关闭明细').trigger('click')
    pending.resolve(manifest()); await flushPromises()
    expect(createUrl).not.toHaveBeenCalled()
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
  })

  it('shows a concise Chinese integrity error and removes stale eligibility', async () => {
    api.downloadTaskDiagnostics.mockRejectedValue(new ApiRequestError(409, '{"error":"DIAGNOSTIC_RAW_INVALID"}'))
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    expect(wrapper.get('[role=alert]').text()).toBe('诊断输出校验失败，暂不可下载。')
    expect(wrapper.text()).not.toContain('DIAGNOSTIC_RAW_INVALID'); expect(createUrl).not.toHaveBeenCalled()
    expect((button(wrapper, '下载诊断 ZIP').element as HTMLButtonElement).disabled).toBe(true)
  })

  it('releases the ObjectURL even if browser saving fails', async () => {
    anchorClick.mockImplementation(() => { throw new Error('synthetic save failure') })
    const wrapper = render(); await flushPromises(); await openDetails(wrapper)
    await button(wrapper, '下载 TXT').trigger('click'); await flushPromises()
    expect(createUrl).toHaveBeenCalledOnce(); expect(revokeUrl).toHaveBeenCalledWith('blob:diagnostic-fixture')
    expect(wrapper.get('[role=alert]').text()).toBe('诊断输出下载失败，请重新打开明细后重试。')
  })
})
