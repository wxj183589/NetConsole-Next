import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import SyslogWorkspace from './SyslogWorkspace.vue'
import { getHealth, request } from '../api/client'
vi.mock('../api/client', () => ({ request: vi.fn(), getHealth: vi.fn().mockResolvedValue({ syslogEnabled: false, collectionState: 'disabled', rawWriteAvailable: true, udpReceived: 0, rawFlushedRecords: 0, udpDropped: 0 }) }))
const button = { template: '<button><slot /></button>' }
const options = { global: { stubs: { 'el-button': button } } }
describe('Syslog workspace', () => {
  it('discards an earlier identity query response and clears cached evidence', async () => {
    let finish!: (value: unknown) => void
    vi.mocked(request).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = mount(SyslogWorkspace, { ...options, props: { identityKey: 'first:SUPERADMIN' } })
    await wrapper.get('form').trigger('submit')
    await wrapper.setProps({ identityKey: 'second:ADMIN' })
    finish({ items: [{ receivedMs: 1000, senderIp: '192.0.2.1', senderPort: 514, facility: 16, severity: 3, message: 'earlier private evidence', rawReference: 'raw/old', line: 1 }], scannedSegments: 1, scannedRecords: 1, unavailableSegments: 0, partial: false })
    await flushPromises()
    expect(wrapper.text()).not.toContain('earlier private evidence')
    expect(wrapper.find('tbody').exists()).toBe(false)
    wrapper.unmount()
  })
  it('shows actual receiver loss and clears stale receiver status after failure', async () => {
    vi.mocked(getHealth).mockResolvedValueOnce({ syslogEnabled: true, collectionState: 'listening', rawWriteAvailable: true, udpReceived: 8, rawFlushedRecords: 6, udpDropped: 2 } as Awaited<ReturnType<typeof getHealth>>)
    const wrapper = mount(SyslogWorkspace, options); await flushPromises()
    expect(wrapper.text()).toContain('发生日志丢弃')
    expect(wrapper.text()).toContain('已接收 8 条')
    vi.mocked(getHealth).mockRejectedValueOnce(new Error('receiver unavailable'))
    await wrapper.findAll('button').find(item => item.text() === '刷新接收状态')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('接收状态读取失败：receiver unavailable')
    expect(wrapper.text()).not.toContain('已接收 8 条')
    wrapper.unmount()
  })
  it('queries exact filters and renders untrusted messages as text with partial evidence', async () => {
    vi.mocked(request).mockResolvedValueOnce({ items: [{ receivedMs: 1000, senderIp: '192.0.2.1', senderPort: 514,
      facility: 16, severity: 3, message: '<img src=x onerror=alert(1)>', rawReference: 'raw/syslog/test.ndjson', line: 1 }],
    scannedSegments: 1, scannedRecords: 2, unavailableSegments: 1, partial: true })
    const wrapper = mount(SyslogWorkspace, options)
    await wrapper.get('[aria-label="Syslog 来源 IP"]').setValue('192.0.2.1')
    await wrapper.get('[aria-label="Syslog 最高级别"]').setValue('3')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(JSON.parse(vi.mocked(request).mock.calls.at(-1)![1]!.body as string)).toMatchObject({ senderIp: '192.0.2.1', maximumSeverity: 3, limit: 200 })
    expect(wrapper.text()).toContain('结果不完整')
    expect(wrapper.text()).toContain('raw/syslog/test.ndjson:1')
    expect(wrapper.find('img').exists()).toBe(false)
    wrapper.unmount()
  })
  it('clears previous result on a failed new query', async () => {
    vi.mocked(request).mockResolvedValueOnce({ items: [], scannedSegments: 0, scannedRecords: 0, unavailableSegments: 0, partial: false })
    const wrapper = mount(SyslogWorkspace, options)
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.text()).toContain('当前条件没有已封存日志')
    vi.mocked(request).mockRejectedValueOnce(new Error('scope denied'))
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.text()).toContain('scope denied')
    expect(wrapper.text()).not.toContain('当前条件没有已封存日志')
    wrapper.unmount()
  })
  it('sends exact event filters and displays server classification with unchanged message and Raw evidence', async () => {
    const message = '<189>%%10WMESH/5/MESH_LINKUP: peer <img src=x> is up'
    vi.mocked(request).mockResolvedValueOnce({ items: [{ receivedMs: 1000, senderIp: '192.0.2.1', senderPort: 514,
      facility: 23, severity: 5, eventFamily: 'WMESH', eventType: 'MESH_LINKUP', message, rawReference: 'raw/syslog/classified.ndjson', line: 3 }],
    scannedSegments: 1, scannedRecords: 1, unavailableSegments: 0, partial: false })
    const wrapper = mount(SyslogWorkspace, options)
    await wrapper.get('[aria-label="Syslog 事件类型"]').setValue('MESH_LINKUP')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(JSON.parse(vi.mocked(request).mock.calls.at(-1)![1]!.body as string)).toMatchObject({ eventType: 'MESH_LINKUP' })
    expect(wrapper.get('tbody').text()).toContain('WMESH / MESH_LINKUP')
    expect(wrapper.get('pre').text()).toBe(message)
    expect(wrapper.get('tbody').text()).toContain('raw/syslog/classified.ndjson:3')
    expect(wrapper.find('img').exists()).toBe(false)
    wrapper.unmount()
  })
  it('retains UNKNOWN classification and clears event evidence when identity changes', async () => {
    vi.mocked(request).mockResolvedValueOnce({ items: [{ receivedMs: 1000, senderIp: '192.0.2.1', senderPort: 514,
      facility: null, severity: null, eventFamily: 'UNKNOWN', eventType: 'UNKNOWN', message: 'unclassified private message', rawReference: 'raw/syslog/unknown.ndjson', line: 1 }],
    scannedSegments: 1, scannedRecords: 1, unavailableSegments: 0, partial: false })
    const wrapper = mount(SyslogWorkspace, { ...options, props: { identityKey: 'first:SUPERADMIN' } })
    await wrapper.get('[aria-label="Syslog 事件类型"]').setValue('UNKNOWN')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(JSON.parse(vi.mocked(request).mock.calls.at(-1)![1]!.body as string).eventType).toBe('UNKNOWN')
    expect(wrapper.get('tbody').text()).toContain('UNKNOWN / UNKNOWN')
    await wrapper.setProps({ identityKey: 'second:ADMIN' })
    expect(wrapper.find('tbody').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('unclassified private message')
    wrapper.unmount()
  })
})
