import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { h, KeepAlive, nextTick, ref } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import FaultCenter from './FaultCenter.vue'

const api = vi.hoisted(() => ({ getFaults: vi.fn(), acknowledgeFault: vi.fn(), processFault: vi.fn(), verifyFault: vi.fn(), setFaultPolicy: vi.fn(), syncFaults: vi.fn() }))
vi.mock('../api/faults', () => api)
const incident = { id: 'fault-a', deviceId: 'device-a', profileId: 'profile-a', siteId: 'site-a', kind: 'INTERFACE_DOWN', resourceKey: 'GigabitEthernet1/0/1', severity: 'WARNING', state: 'OPEN', firstSeenMs: 1000, lastSeenMs: 2000, evidenceType: 'FACT', evidenceId: 'fact-a', detail: 'observed DOWN', occurrences: 2, history: [{ atMs: 2000, action: 'DETECTED', actorUserId: 'actor', evidenceId: 'fact-a', detail: 'Raw verified' }] }
const result = { page: 1, pageSize: 25, total: 1, hasMore: false, items: [incident], policies: [], limitations: ['1000 incidents / 50 transitions'] }
const router = () => createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { render: () => null } }] })
const render = (canRecover = true) => mount(FaultCenter, { props: { canRecover }, global: { plugins: [router()] } })
const button = (wrapper: ReturnType<typeof render>, text: string) => wrapper.findAll('button').find(b => b.text() === text)!

describe('FaultCenter', () => {
  beforeEach(() => { vi.resetAllMocks(); api.getFaults.mockResolvedValue(structuredClone(result)); api.setFaultPolicy.mockResolvedValue({ state: 'SCHEDULED' }); api.verifyFault.mockResolvedValue({ state: 'RECOVERY_NOT_VERIFIED', detail: '缺少恢复证据' }) })
  it('explains live Long Ping evidence and separates pause and protocol recovery boundaries', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.text()).toContain('运行中的 Long Ping 按真实样本同步故障、恢复与复发')
    expect(wrapper.text()).toContain('暂停和取消不提供恢复证据')
    expect(wrapper.text()).toContain('ICMP 与 TCP 端口分别核验')
    expect(wrapper.text()).toContain('累计丢包率保留')
    expect(api.syncFaults).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('filters lost LLDP neighbors and exposes the exact evidence partition', async () => {
    api.getFaults.mockResolvedValue({ ...result, items: [{ ...incident, kind: 'LLDP_NEIGHBOR_LOST', sourceAuthority: 'H3C_TASK_RAW', capturedScopeKey: '["scope-a"]' }] })
    const wrapper = render(); await flushPromises()
    await wrapper.get('[aria-label="故障类型"]').setValue('LLDP_NEIGHBOR_LOST')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(api.getFaults.mock.calls.at(-1)?.[1].kind).toBe('LLDP_NEIGHBOR_LOST')
    await button(wrapper, '查看处理历史').trigger('click')
    expect(wrapper.text()).toContain('H3C_TASK_RAW'); expect(wrapper.text()).toContain('["scope-a"]')
    expect(wrapper.text()).toContain('完整 LLDP 表')
    wrapper.unmount()
  })
  it('loads durable events without admitting device actions and expands evidence history', async () => {
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('OPEN'); expect(wrapper.text()).toContain('2 次有效观测')
    expect(api.setFaultPolicy).not.toHaveBeenCalled(); expect(api.syncFaults).not.toHaveBeenCalled()
    await button(wrapper, '查看处理历史').trigger('click')
    expect(wrapper.text()).toContain('Raw verified'); expect(wrapper.text()).toContain('默认关闭')
    wrapper.unmount()
  })
  it('confirms and verifies without falsely changing lifecycle from a blocked verification', async () => {
    const wrapper = render(); await flushPromises()
    await button(wrapper, '确认故障').trigger('click'); await flushPromises()
    expect(api.acknowledgeFault).toHaveBeenCalledWith('fault-a')
    await button(wrapper, '核验恢复').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('RECOVERY_NOT_VERIFIED'); expect(wrapper.text()).toContain('OPEN')
    wrapper.unmount()
  })
  it('requires explicit bounded target policy and shows the durable plan rather than recovery success', async () => {
    const wrapper = render(); await flushPromises(); await button(wrapper, '查看处理历史').trigger('click')
    await button(wrapper, '启用有限只读补采').trigger('click'); await flushPromises()
    expect(api.setFaultPolicy).toHaveBeenCalledWith(incident, true, 5, 3)
    expect(wrapper.text()).toContain('SCHEDULED'); expect(wrapper.text()).toContain('不代表业务恢复')
    api.getFaults.mockResolvedValue({ ...result, policies: [{ deviceId: 'device-a', state: 'AWAITING_RECOLLECTION', enabled: true, attempts: 1, maximumAttempts: 3, planId: 'plan-a', nextRunMs: 10000 }] })
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.get('a[href*="/task-plans/"]').attributes('href')).toContain('/task-plans/plan-a/report')
    expect(button(wrapper, '启用有限只读补采').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('shows full filtered statistics and starts processing reopened incidents without claiming recovery', async () => {
    api.getFaults.mockResolvedValue({ ...result, items: [{ ...incident, state: 'REOPENED' }], summary: { open: 2, acknowledged: 3, processing: 28, resolved: 4, reopened: 1, affectedDevices: 7 } })
    const wrapper = render(); await flushPromises()
    expect(wrapper.get('[aria-label="筛选范围故障统计"]').text()).toContain('处理中 28')
    expect(wrapper.get('[aria-label="筛选范围故障统计"]').text()).toContain('涉及设备 7')
    expect(button(wrapper, '确认故障').attributes('disabled')).toBeUndefined()
    await button(wrapper, '开始处理').trigger('click'); await flushPromises()
    expect(api.processFault).toHaveBeenCalledWith('fault-a')
    expect(wrapper.text()).toContain('仍等待同资源恢复证据')
    api.getFaults.mockResolvedValue({ ...result, items: [{ ...incident, state: 'PROCESSING' }] })
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(button(wrapper, '开始处理').attributes('disabled')).toBeDefined()
    expect(button(wrapper, '确认故障').attributes('disabled')).toBeDefined()
    expect(button(wrapper, '核验恢复').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
  it('hides mutation controls from readers and keeps history query usable', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.text()).not.toContain('确认故障'); expect(wrapper.text()).not.toContain('同步当前证据')
    expect(wrapper.text()).not.toContain('开始处理')
    await wrapper.findAll('select')[0]!.setValue('RESOLVED')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(api.getFaults).toHaveBeenLastCalledWith(1, expect.objectContaining({ state: 'RESOLVED' }))
    wrapper.unmount()
  })
  it('queries traceroute anomalies from the fault kind filter', async () => {
    const wrapper = render(false); await flushPromises()
    await wrapper.get('[aria-label="故障类型"]').setValue('TRACEROUTE_ANOMALY')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(api.getFaults).toHaveBeenLastCalledWith(1, expect.objectContaining({ kind: 'TRACEROUTE_ANOMALY' }))
    wrapper.unmount()
  })
  it('clears stale records on history error and renders an empty scope', async () => {
    const wrapper = render(); await flushPromises()
    api.getFaults.mockRejectedValueOnce(new Error('store unavailable'))
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('store unavailable'); expect(wrapper.text()).not.toContain('observed DOWN')
    api.getFaults.mockResolvedValueOnce({ ...result, total: 0, items: [] })
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('没有故障记录'); wrapper.unmount()
  })
  it('keeps reader navigation scoped to exact device identity and resets the first-page filter', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.get('a[href^="/config-backups"]').attributes('href')).toBe('/config-backups?deviceId=device-a')
    await wrapper.get('[aria-label="故障状态"]').setValue('OPEN')
    await button(wrapper, '仅看此设备故障').trigger('click'); await flushPromises()
    expect(api.getFaults).toHaveBeenLastCalledWith(1, expect.objectContaining({ deviceId: 'device-a', state: 'OPEN' }))
    await button(wrapper, '重置故障筛选').trigger('click'); await flushPromises()
    expect(api.getFaults).toHaveBeenLastCalledWith(1, { deviceId: '', state: '', kind: '', fromMs: undefined, toMs: undefined })
    expect(api.setFaultPolicy).not.toHaveBeenCalled(); expect(api.syncFaults).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('uses evidence-type-specific read-only links and never invents missing source targets', async () => {
    api.getFaults.mockResolvedValue({ ...result, items: [
      { ...incident, evidenceType: 'TASK_RUN', evidenceId: 'task/a ?' },
      { ...incident, id: 'fault-b', profileId: '', evidenceType: 'DIAGNOSTIC', evidenceId: 'probe#b' },
      { ...incident, id: 'fault-c', evidenceType: 'WIRELESS_RUNTIME', evidenceId: 'ap:c' },
      { ...incident, id: 'fault-d', profileId: '', evidenceType: 'UNRECOGNIZED', evidenceId: 'x' },
    ] })
    const wrapper = render(false); await flushPromises()
    const sources=wrapper.findAll('a[target="_blank"]')
    expect(sources.map(link => link.attributes('href'))).toEqual(['/api/system/tasks/task%2Fa%20%3F', '/api/system/diagnostics/runs/probe%23b', '/api/system/wireless/ap-resources/ap%3Ac/runtime-state'])
    expect(sources.every(link => link.attributes('rel') === 'noopener')).toBe(true)
    expect(wrapper.findAll('a[href^="/config-backups"]').length).toBe(2)
    wrapper.unmount()
  })
  it('invalidates a hidden response and refreshes when KeepAlive reactivates', async () => {
    let complete!: (value: typeof result) => void
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const shown = ref(true)
    const wrapper = mount({ render: () => h(KeepAlive, null, { default: () => shown.value ? h(FaultCenter) : h('div', 'hidden') }) }, { global: { plugins: [router()] } })
    await nextTick(); shown.value = false; await nextTick(); complete(result); await flushPromises()
    shown.value = true; await flushPromises()
    expect(api.getFaults).toHaveBeenCalledTimes(2); expect(wrapper.text()).toContain('故障中心')
    wrapper.unmount()
  })
  it('exports only the loaded page with applied filters, all retained history and formula-safe redacted text', async () => {
    const parts: BlobPart[][] = []; let filename = ''
    const create = vi.fn(() => 'blob:faults'); const revoke = vi.fn()
    vi.stubGlobal('URL', { createObjectURL: create, revokeObjectURL: revoke })
    vi.stubGlobal('Blob', class { constructor(value: BlobPart[]) { parts.push(value) } })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function(this: HTMLAnchorElement) { filename = this.download })
    api.getFaults.mockResolvedValue({ ...result, page: 2, total: 80, hasMore: true, items: [{ ...incident, resourceKey: ' \t=SUM(1,2)', sourceAuthority: 'H3C_TASK_RAW', capturedScopeKey: '["scope-a"]', detail: 'password="hidden word"; Authorization: Bearer abc-secret; https://user:pass@example.test/\nnext "quoted"', history: [{ ...incident.history[0], action: '@CMD', detail: 'password cipher h3c-secret; api_key=api-secret; -----BEGIN PRIVATE KEY-----\nprivate-secret\n-----END PRIVATE KEY-----' }] }] })
    const wrapper = render(false); await flushPromises()
    await wrapper.get('[aria-label="设备 ID"]').setValue('edited-but-not-applied')
    try {
      await button(wrapper, '导出当前页故障与历史 CSV').trigger('click')
      const csv = String(parts[0]![0])
      expect(filename).toBe('fault-history-page-2.csv')
      expect(csv).toContain('CURRENT_PAGE_LOADED_QUERY_ONLY","2","25","80"')
      expect(csv).toContain('"EVENT","fault-a","device-a","profile-a","site-a"')
      expect(csv).toContain('"HISTORY","fault-a"')
      expect(csv).toContain('source_authority'); expect(csv).toContain('captured_scope_key')
      expect(csv).toContain('H3C_TASK_RAW'); expect(csv).toContain('[""scope-a""]')
      expect(csv).toContain("' \t=SUM(1,2)"); expect(csv).toContain("'@CMD")
      expect(csv).toContain('next ""quoted""'); expect(csv).toContain('[REDACTED]')
      expect(csv).toContain('1970-01-01T00:00:02.000Z'); expect(csv).toContain('"actor","fact-a"')
      for (const secret of ['hidden word','abc-secret','user:pass','h3c-secret','api-secret','private-secret','edited-but-not-applied']) expect(csv).not.toContain(secret)
      expect(api.getFaults).toHaveBeenCalledTimes(1); expect(api.syncFaults).not.toHaveBeenCalled()
      expect(create).toHaveBeenCalledOnce(); expect(revoke).toHaveBeenCalledWith('blob:faults')
    } finally { wrapper.unmount(); click.mockRestore(); vi.unstubAllGlobals() }
  })
  it('disables export while loading and for empty or failed queries', async () => {
    let complete!: (value: typeof result) => void
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    const wrapper = render(false)
    expect(button(wrapper, '导出当前页故障与历史 CSV').attributes('disabled')).toBeDefined()
    complete({ ...result, items: [], total: 0 }); await flushPromises()
    expect(button(wrapper, '导出当前页故障与历史 CSV').attributes('disabled')).toBeDefined()
    api.getFaults.mockRejectedValueOnce(new Error('query denied'))
    await button(wrapper, '刷新故障历史').trigger('click'); await flushPromises()
    expect(button(wrapper, '导出当前页故障与历史 CSV').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('invalidates exported facts and pending old responses when the mounted identity changes', async () => {
    const wrapper = render(false); await flushPromises()
    expect(wrapper.text()).toContain('observed DOWN')
    let oldComplete!: (value: typeof result) => void
    let newComplete!: (value: typeof result) => void
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { oldComplete = resolve }))
    await button(wrapper, '刷新故障历史').trigger('click')
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { newComplete = resolve }))
    await wrapper.setProps({ identityKey: 'reader-b:ADMIN' })
    expect(wrapper.text()).not.toContain('observed DOWN')
    expect(wrapper.find('a[href^="/config-backups"]').exists()).toBe(false)
    expect(button(wrapper, '导出当前页故障与历史 CSV').attributes('disabled')).toBeDefined()
    oldComplete(result); await flushPromises()
    expect(wrapper.text()).not.toContain('observed DOWN')
    newComplete({ ...result, items: [], total: 0 }); await flushPromises()
    expect(wrapper.text()).toContain('当前筛选没有故障记录')
    expect(button(wrapper, '导出当前页故障与历史 CSV').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('clears the loaded snapshot while hidden and defers identity reload until activation', async () => {
    const shown = ref(true); const identity = ref('reader-a:ADMIN')
    const panel = { render: () => h(FaultCenter, { identityKey: identity.value }) }
    const wrapper = mount({ render: () => h(KeepAlive, null, { default: () => shown.value ? h(panel) : h('div', 'hidden') }) }, { global: { plugins: [router()] } })
    await flushPromises(); shown.value = false; await nextTick()
    identity.value = 'reader-b:ADMIN'; await nextTick()
    expect(api.getFaults).toHaveBeenCalledTimes(1)
    let complete!: (value: typeof result) => void
    api.getFaults.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    shown.value = true; await nextTick()
    expect(wrapper.text()).not.toContain('observed DOWN')
    expect(wrapper.findAll('button').find(b => b.text() === '导出当前页故障与历史 CSV')!.attributes('disabled')).toBeDefined()
    complete({ ...result, items: [], total: 0 }); await flushPromises()
    expect(api.getFaults).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })
  it('protects formula prefixes behind control/format characters and redacts JSON and truncated credential output', async () => {
    const parts: BlobPart[][] = []
    vi.stubGlobal('URL', { createObjectURL: vi.fn(() => 'blob:faults'), revokeObjectURL: vi.fn() })
    vi.stubGlobal('Blob', class { constructor(value: BlobPart[]) { parts.push(value) } })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    api.getFaults.mockResolvedValue({ ...result, items: [{ ...incident, resourceKey: '\u0000\u001b=CMD', detail: '{"password":"json-secret\\"escaped-secret", "access_token":"access-secret", "authorization":"Basic basic-secret"}; password="unterminated secret', history: [{ ...incident.history[0], actorUserId: '\u200b+CMD', detail: 'snmp-agent community read snmp-secret; -----BEGIN RSA PRIVATE KEY-----\ntruncated-secret' }] }] })
    const wrapper = render(false); await flushPromises()
    try {
      await button(wrapper, '导出当前页故障与历史 CSV').trigger('click')
      const csv = String(parts[0]![0])
      expect(csv).toContain("'\u0000\u001b=CMD"); expect(csv).toContain("'\u200b+CMD")
      for (const secret of ['json-secret','escaped-secret','access-secret','basic-secret','snmp-secret','truncated-secret','unterminated secret']) expect(csv).not.toContain(secret)
      expect(csv).toContain('[REDACTED PRIVATE KEY]')
    } finally { wrapper.unmount(); click.mockRestore(); vi.unstubAllGlobals() }
  })
})
