import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { TerminalSession, TerminalSessionPage } from '../api/client'
import { useAuthStore } from '../stores/auth'
import TerminalView from './TerminalView.vue'

const api = vi.hoisted(() => ({
  ApiRequestError: class extends Error { constructor(public status: number, message: string) { super(message) } },
  getTerminalDevices: vi.fn(), getLinuxServers: vi.fn(), getTerminalSessions: vi.fn(), getTerminalSession: vi.fn(),
  getTerminalEvents: vi.fn(), getRecordingIntegrity: vi.fn(), getReplay: vi.fn(), downloadExport: vi.fn(),
  closeTerminalSession: vi.fn(), createTerminalSession: vi.fn(), createLinuxServerSession: vi.fn(),
  issueTerminalTicket: vi.fn(), getSerialPorts: vi.fn(),
}))
const push = vi.hoisted(() => vi.fn())
vi.mock('../api/client', () => api)
vi.mock('vue-router', () => ({ useRoute: () => ({ path: '/terminal', fullPath: '/terminal', query: {} }), useRouter: () => ({ push }) }))
vi.mock('element-plus', () => ({ ElMessage: { info: vi.fn(), error: vi.fn() } }))
vi.mock('@xterm/xterm', () => ({ Terminal: class {} }))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class {} }))
vi.mock('@xterm/addon-search', () => ({ SearchAddon: class {} }))

function session(index: number): TerminalSession {
  return { id: `session-${index}`, targetId: `device-${index}`, targetName: `历史设备 ${index}`, protocol: 'SSH',
    state: 'CLOSED', startedMs: 1700000000000 - index * 1000, recordingStatus: 'COMPLETE', recordingComplete: true,
    commandCount: 1, outputBytes: 20, recordingBytes: 100 }
}
function page(number = 1, total = 25, pageSize = 10): TerminalSessionPage {
  return { page: number, pageSize, total, items: Array.from({ length: Math.min(pageSize, Math.max(0, total - (number - 1) * pageSize)) }, (_, index) => session((number - 1) * pageSize + index + 1)) }
}
function deferred<T>() {
  let resolve!: (value: T) => void; let reject!: (cause: unknown) => void
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail })
  return { promise, resolve, reject }
}
const wrappers: VueWrapper[] = []
async function render() {
  const pinia = createPinia(); setActivePinia(pinia)
  useAuthStore().user = { username: 'tester', role: 'ADMIN' }
  const wrapper = mount(TerminalView, { global: { plugins: [pinia], stubs: {
    NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
    'el-icon': { template: '<span><slot/></span>' },
    'el-button': { props: ['loading', 'disabled', 'nativeType'], template: '<button :type="nativeType || \'button\'" :disabled="disabled || loading"><slot/></button>' },
    'el-dialog': { props: ['modelValue'], emits: ['close'], template: '<section v-if="modelValue" role="dialog"><button @click="$emit(\'close\')">关闭回放</button><slot/><slot name="footer"/></section>' },
  } } })
  wrappers.push(wrapper); await flushPromises(); return wrapper
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(value => value.text() === text)! }
async function openRow(wrapper: VueWrapper, index = 0) {
  await wrapper.findAll('[data-testid=terminal-history-row]')[index].get('button').trigger('click'); await flushPromises()
}
async function closeReplay(wrapper: VueWrapper) { await button(wrapper, '关闭回放').trigger('click'); await flushPromises() }

describe('terminal session history and replay', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getTerminalDevices.mockResolvedValue([]); api.getLinuxServers.mockResolvedValue([])
    api.getTerminalSessions.mockImplementation(({ page: number, pageSize }) => Promise.resolve(page(number, 25, pageSize)))
    api.getTerminalEvents.mockResolvedValue([{ atMs: 1700000000000, eventType: 'CLOSED', detailJson: '正常结束' }])
    api.getRecordingIntegrity.mockResolvedValue({ status: 'COMPLETE', detail: '', hashMatches: true })
    api.getReplay.mockResolvedValue({ entries: [{ atMs: 1, direction: 'output', dataB64: btoa('display [a.b]') }], hasMore: false })
    api.downloadExport.mockResolvedValue(new Blob(['recording']))
  })
  afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers(); vi.unstubAllGlobals() })

  it('shows more than six sessions and replays an older row without creating a live connection', async () => {
    const wrapper = await render()
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(10)
    expect(wrapper.text()).toContain('共 25 条 · 第 1 / 3 页')
    await openRow(wrapper, 7)
    expect(api.getTerminalEvents).toHaveBeenCalledWith('session-8')
    expect(api.getReplay).toHaveBeenCalledWith('session-8', 0)
    expect(wrapper.get('[role=dialog]').text()).toContain('SHA-256 匹配')
    expect(wrapper.get('[role=dialog]').text()).toContain('正常结束')
    await button(wrapper, '播放').trigger('click')
    expect(wrapper.get('.history-replay').text()).toContain('display [a.b]')
    await wrapper.get('input[placeholder="搜索回放"]').setValue('[a.b]')
    expect(wrapper.get('.history-replay').text()).toContain('【[a.b]】')
    expect(api.createTerminalSession).not.toHaveBeenCalled(); expect(api.createLinuxServerSession).not.toHaveBeenCalled()
  })

  it('pages through all results and resets to page one when the query or page size changes', async () => {
    const wrapper = await render()
    await button(wrapper, '下一页').trigger('click'); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith({ page: 2, pageSize: 10, keyword: '', protocol: '' })
    expect(wrapper.findAll('[data-testid=terminal-history-row]')[0].text()).toContain('历史设备 11')
    await button(wrapper, '下一页').trigger('click'); await flushPromises()
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(5)
    expect(button(wrapper, '下一页').attributes('disabled')).toBeDefined()
    await button(wrapper, '上一页').trigger('click'); await flushPromises()
    await wrapper.get('input[aria-label="搜索会话历史"]').setValue('  older & target  ')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith({ page: 1, pageSize: 10, keyword: 'older & target', protocol: '' })
    await wrapper.get('select[aria-label="历史协议"]').setValue('TELNET'); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, protocol: 'TELNET' }))
    await wrapper.get('select[aria-label="每页会话数"]').setValue('20'); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 20 }))
  })

  it('makes replay selection and the task center navigation functional', async () => {
    const wrapper = await render()
    await button(wrapper, '终端回放').trigger('click')
    expect(wrapper.text()).toContain('请从会话历史选择一条记录')
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(0)
    await button(wrapper, '选择会话').trigger('click'); await openRow(wrapper)
    await closeReplay(wrapper)
    expect(wrapper.text()).toContain('历史设备 1')
    await button(wrapper, '打开回放 / 导出').trigger('click'); await flushPromises()
    expect(api.getReplay).toHaveBeenCalledTimes(2)
    await closeReplay(wrapper); await button(wrapper, '任务记录 ↗').trigger('click')
    expect(push).toHaveBeenCalledWith('/tasks')
  })

  it('clears rows after a failed page and retries the requested page', async () => {
    const wrapper = await render()
    api.getTerminalSessions.mockRejectedValueOnce(new Error('offline'))
    await button(wrapper, '下一页').trigger('click'); await flushPromises()
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(0)
    expect(wrapper.get('[role=alert]').text()).toContain('会话历史读取失败')
    await button(wrapper, '重试历史').trigger('click'); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }))
    expect(wrapper.findAll('[data-testid=terminal-history-row]')[0].text()).toContain('历史设备 11')
  })

  it.each(['resolve', 'reject'] as const)('ignores a late history %s after a new search', async completion => {
    const wrapper = await render(); const old = deferred<TerminalSessionPage>()
    api.getTerminalSessions.mockReturnValueOnce(old.promise)
    await button(wrapper, '下一页').trigger('click')
    api.getTerminalSessions.mockResolvedValueOnce({ ...page(1, 1), items: [{ ...session(99), targetName: '当前查询' }] })
    await wrapper.get('input[aria-label="搜索会话历史"]').setValue('current')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    if (completion === 'resolve') old.resolve(page(2)); else old.reject(new api.ApiRequestError(403, 'old permission error'))
    await flushPromises()
    expect(wrapper.text()).toContain('当前查询'); expect(wrapper.text()).not.toContain('历史设备 11')
    expect(wrapper.find('[role=alert]').exists()).toBe(false)
  })

  it('loads all replay pages using server offsets', async () => {
    const wrapper = await render()
    api.getReplay.mockResolvedValueOnce({ entries: [{ atMs: 1, direction: 'output', dataB64: btoa('first') }], hasMore: true })
      .mockResolvedValueOnce({ entries: [{ atMs: 2, direction: 'output', dataB64: btoa('second') }], hasMore: false })
    await openRow(wrapper)
    expect(api.getReplay.mock.calls).toEqual([['session-1', 0], ['session-1', 1]])
    vi.useFakeTimers(); await button(wrapper, '播放').trigger('click'); await vi.runAllTimersAsync()
    expect(wrapper.get('.history-replay').text()).toContain('firstsecond')
  })

  it('ignores closed replay responses and keeps a newly selected session intact', async () => {
    const wrapper = await render(); const old = deferred<{ entries: { atMs: number; direction: string; dataB64: string }[]; hasMore: boolean }>()
    api.getReplay.mockReturnValueOnce(old.promise)
    await openRow(wrapper); await closeReplay(wrapper)
    await button(wrapper, '会话历史').trigger('click'); await openRow(wrapper, 1)
    old.resolve({ entries: [{ atMs: 1, direction: 'output', dataB64: btoa('old private output') }], hasMore: false }); await flushPromises()
    expect(wrapper.get('[role=dialog]').text()).toContain('历史设备 2')
    await button(wrapper, '播放').trigger('click')
    expect(wrapper.get('.history-replay').text()).toContain('display')
    expect(wrapper.text()).not.toContain('old private output')
  })

  it('does not start replay downloads when metadata arrives after closing', async () => {
    const wrapper = await render(); const pending = deferred<unknown[]>()
    api.getTerminalEvents.mockReturnValueOnce(pending.promise)
    await openRow(wrapper); await closeReplay(wrapper)
    pending.resolve([]); await flushPromises()
    expect(api.getReplay).not.toHaveBeenCalled(); expect(wrapper.find('[role=dialog]').exists()).toBe(false)
  })

  it.each([401, 403, 404])('clears history and pending replay after a %i history denial', async status => {
    const wrapper = await render(); const pending = deferred<unknown>()
    api.getReplay.mockReturnValueOnce(pending.promise); await openRow(wrapper)
    api.getTerminalSessions.mockRejectedValueOnce(new api.ApiRequestError(status, 'denied'))
    await button(wrapper, '刷新历史').trigger('click'); await flushPromises()
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
    expect(wrapper.get('[role=alert]').text()).toContain('当前账户无权访问')
    pending.resolve({ entries: [{ atMs: 1, direction: 'output', dataB64: btoa('denied output') }], hasMore: false }); await flushPromises()
    await button(wrapper, '会话历史').trigger('click')
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(0)
    expect(wrapper.text()).not.toContain('denied output')
  })

  it('clears history after replay permission is revoked', async () => {
    const wrapper = await render()
    api.getReplay.mockRejectedValueOnce(new api.ApiRequestError(403, 'site scope changed'))
    await openRow(wrapper)
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
    expect(wrapper.get('[role=alert]').text()).toContain('当前账户无权访问')
    await button(wrapper, '会话历史').trigger('click')
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(0)
  })

  it('allows retry after a replay read error with stale metadata cleared', async () => {
    const wrapper = await render(); api.getReplay.mockRejectedValueOnce(new Error('offline'))
    await openRow(wrapper)
    expect(wrapper.get('[role=dialog] [role=alert]').text()).toContain('会话回放读取失败')
    expect(wrapper.get('[role=dialog]').text()).not.toContain('正常结束')
    expect(button(wrapper, 'TXT').attributes('disabled')).toBeDefined()
    await button(wrapper, '重试回放').trigger('click'); await flushPromises()
    expect(wrapper.find('[role=dialog] [role=alert]').exists()).toBe(false)
    expect(wrapper.get('[role=dialog]').text()).toContain('正常结束')
  })

  it('invalidates pending replay and exports on sign out', async () => {
    const wrapper = await render(); await openRow(wrapper)
    const pendingExport = deferred<Blob>(); api.downloadExport.mockReturnValueOnce(pendingExport.promise)
    const createUrl = vi.spyOn(URL, 'createObjectURL')
    await button(wrapper, 'TXT').trigger('click')
    useAuthStore().user = null
    pendingExport.resolve(new Blob(['private export'])); await flushPromises()
    expect(createUrl).not.toHaveBeenCalled(); createUrl.mockRestore()
    expect(wrapper.find('[role=dialog]').exists()).toBe(false)
    await button(wrapper, '会话历史').trigger('click')
    expect(wrapper.findAll('[data-testid=terminal-history-row]')).toHaveLength(0)
  })

  it('discards previous permissions and ignores a pending list on role change', async () => {
    const wrapper = await render(); const old = deferred<TerminalSessionPage>()
    api.getTerminalSessions.mockReturnValueOnce(old.promise)
    await button(wrapper, '下一页').trigger('click')
    api.getTerminalSessions.mockResolvedValueOnce({ ...page(1, 1), items: [{ ...session(50), targetName: '当前授权会话' }] })
    useAuthStore().user = { username: 'tester', role: 'SUPERADMIN' }; await flushPromises()
    old.resolve(page(2)); await flushPromises()
    expect(api.getTerminalSessions).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1 }))
    expect(wrapper.text()).toContain('当前授权会话'); expect(wrapper.text()).not.toContain('历史设备 11')
  })
})
