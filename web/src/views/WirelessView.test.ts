import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WirelessAcDomain, WirelessApGroup, WirelessApGroupDetail } from '../api/client'
import WirelessView from './WirelessView.vue'

const api = vi.hoisted(() => ({
  createWirelessTask: vi.fn(), getH3cTaskPlan: vi.fn(), getWirelessTasks: vi.fn(), getWirelessTask: vi.fn(), downloadWirelessTask: vi.fn(), cancelH3cTaskPlan: vi.fn(),
  getWirelessAcDomains: vi.fn(), getWirelessApGroup: vi.fn(), getWirelessApGroups: vi.fn(),
  getWirelessApResources: vi.fn(), getWirelessCollectionManifest: vi.fn(),
  getWirelessCollectionManifests: vi.fn(), getWirelessRuntimeState: vi.fn(),
}))
vi.mock('../api/client', () => api)
vi.mock('element-plus', () => ({ ElMessage: { error: vi.fn() } }))
enableAutoUnmount(afterEach)

const domain: WirelessAcDomain = {
  id: 'domain-one', displayName: 'Synthetic Domain', domainKind: 'AC_DOMAIN', topologyStatus: 'CONFIRMED',
  memberDeviceIds: ['ac-a', 'ac-b'], sourceEvidenceRefs: ['synthetic-domain-evidence'],
  siteScopeStatus: 'FULL', revision: 'domain-revision', asOf: 1_800_000_000_000,
  members: ['ac-a', 'ac-b'].map(deviceId => ({
    deviceId, displayName: deviceId, siteId: 'site-one', managementIp: '192.0.2.1', vendor: 'H3C',
    model: 'Synthetic', role: 'UNKNOWN', runtimeState: 'UNKNOWN', connectionStatus: 'UNKNOWN', enabled: true,
  })),
}
function group(id: string, sourceAc = 'ac-a', changes: Partial<WirelessApGroup> = {}): WirelessApGroup {
  return {
    id, sourceAcDeviceId: sourceAc, sourceAcDomainId: domain.id, sourceGroupKey: 'source-key',
    displayName: '同名组', groupScope: 'AC', groupIdentityKey: `AC|${sourceAc}|source-key`,
    groupIdentityStatus: 'CONFIRMED', memberResourceIds: [], membershipRevision: `membership-${id}`,
    sourceObservationIds: [`observation-${id}`], sourceAcDeviceIds: [sourceAc],
    sourceEvidenceRefs: [`synthetic-raw-${id}`], observedAtMs: domain.asOf,
    conflictReason: null, sourceRevision: `source-${id}`, scopeStatus: 'FULL', ...changes,
  }
}
function detail(value: WirelessApGroup): WirelessApGroupDetail {
  return { group: value, members: [], observations: [], total: 0, asOf: domain.asOf,
    scope: { status: 'FULL', claimsRevision: 'synthetic-claims' }, sourceRevision: value.sourceRevision,
    warnings: ['OFFLINE_FIXTURE_EVIDENCE'], provenance: { ...value } }
}
function page<T>(items: T[]) { return { items, hasMore: false, warnings: ['OFFLINE_FIXTURE_EVIDENCE'], sourceRevision: 'synthetic-revision' } }
function render() {
  return mount(WirelessView, { global: {
    directives: { loading: () => undefined },
    stubs: {
      NcLayout: { template: '<main><slot name="actions"/><slot/></main>' },
      'el-button': { template: '<button><slot/></button>' },
      'el-alert': { props: ['title'], template: '<div role="alert">{{ title }}</div>' },
      'el-pagination': true,
    },
  } })
}

describe('AP group source identity', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    api.getWirelessTasks.mockResolvedValue([])
    api.getWirelessAcDomains.mockResolvedValue(page([domain]))
    api.getWirelessApResources.mockResolvedValue(page([]))
    api.getWirelessCollectionManifests.mockResolvedValue(page([]))
    api.getWirelessApGroups.mockResolvedValue(page([]))
    api.getWirelessApGroup.mockImplementation(async (id: string) => detail(group(id)))
  })

  it('creates a read-only task for selected domains and refreshes its state', async () => {
    const plan = { id: 'plan-one', state: 'QUEUED', items: [{ ordinal: 0, target: 'ac-a', state: 'QUEUED' }] }
    api.createWirelessTask.mockResolvedValue({ id: plan.id, plan })
    api.getH3cTaskPlan.mockResolvedValue({ ...plan, state: 'RUNNING' })
    const wrapper = render()
    await flushPromises()
    await wrapper.get('.wireless-task input').setValue(true)
    await wrapper.findAll('.wireless-task button')[0].trigger('click')
    await flushPromises()
    expect(api.createWirelessTask).toHaveBeenCalledWith(['domain-one'], ['display version'], undefined)
    expect(wrapper.get('.wireless-task-result').text()).toContain('QUEUED')
    await wrapper.findAll('.wireless-task button')[1].trigger('click')
    await flushPromises()
    expect(api.getH3cTaskPlan).toHaveBeenCalledWith('plan-one')
    expect(wrapper.get('.wireless-task-result').text()).toContain('RUNNING')
  })

  it('loads task history and reads a selected report', async () => {
    const summary = { id: 'history-one', state: 'FAILED', createdMs: domain.asOf, total: 1, succeeded: 0, failed: 1 }
    api.getWirelessTasks.mockResolvedValue([summary])
    api.getWirelessTask.mockResolvedValue({ summary, items: [{ ordinal: 0, deviceId: 'ac-a', state: 'FAILED', error: 'TIMEOUT' }] })
    const wrapper = render()
    await flushPromises()
    await wrapper.get('.wireless-task-row button').trigger('click')
    await flushPromises()
    expect(api.getWirelessTask).toHaveBeenCalledWith('history-one')
    expect(wrapper.get('.wireless-task-detail').text()).toContain('ac-a')
    expect(wrapper.get('.wireless-task-detail').text()).toContain('TIMEOUT')
  })

  it('does not restore an old task detail after a newer selection', async () => {
    const first = { id: 'first', state: 'FAILED', createdMs: domain.asOf, updatedMs: domain.asOf, total: 1, succeeded: 0, failed: 1 }
    const second = { ...first, id: 'second' }
    api.getWirelessTasks.mockResolvedValue([first, second])
    let resolveFirst!: (value: unknown) => void
    api.getWirelessTask.mockImplementation((id: string) => id === 'first'
      ? new Promise(resolve => { resolveFirst = resolve })
      : Promise.resolve({ summary: second, items: [] }))
    const wrapper = render()
    await flushPromises()
    await wrapper.findAll('.wireless-task-row button')[0].trigger('click')
    await wrapper.findAll('.wireless-task-row button')[1].trigger('click')
    await flushPromises()
    resolveFirst({ summary: first, items: [] })
    await flushPromises()
    expect(wrapper.get('.wireless-task-detail').text()).toContain('second')
    expect(wrapper.get('.wireless-task-detail').text()).not.toContain('first')
  })

  it('shows task creation errors', async () => {
    api.createWirelessTask.mockRejectedValue(new Error('权限不足'))
    const wrapper = render()
    await flushPromises()
    await wrapper.get('.wireless-task input').setValue(true)
    await wrapper.findAll('.wireless-task button')[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('.wireless-task [role=alert]').text()).toContain('权限不足')
  })

  it('keeps same-name groups separate and reads details using the projection ID', async () => {
    const first = group('projection-a'), second = group('projection-b', 'ac-b')
    api.getWirelessApGroups.mockResolvedValue(page([first, second]))
    api.getWirelessApGroup.mockResolvedValue(detail(second))
    const wrapper = render()
    await flushPromises()
    const rows = wrapper.findAll('.wireless-group-row')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('AC 来源 · ac-a')
    expect(rows[1].text()).toContain('AC|ac-b|source-key')
    await rows[1].trigger('click')
    await flushPromises()
    expect(api.getWirelessApGroup).toHaveBeenCalledWith('projection-b', { sourceAcId: undefined })
    const provenance = wrapper.get('.wireless-group-detail').text()
    for (const value of ['projection-b', 'source-key', 'source-projection-b', 'membership-projection-b', 'observation-projection-b', 'synthetic-raw-projection-b']) {
      expect(provenance).toContain(value)
    }
    expect(provenance).not.toContain('observation-projection-a')
    expect(wrapper.get('[role=alert]').text()).toContain('非模拟器实时状态')
  })

  it('shows candidate and unknown evidence without turning a display name into a stable key', async () => {
    const candidate = group('candidate', 'ac-a', { sourceGroupKey: null, groupIdentityKey: null, groupIdentityStatus: 'CANDIDATE' })
    const unknown = group('unknown', '', { groupScope: 'UNKNOWN', sourceGroupKey: null, groupIdentityKey: null,
      groupIdentityStatus: 'UNKNOWN', sourceAcDeviceIds: [] })
    api.getWirelessApGroups.mockResolvedValue(page([candidate, unknown]))
    api.getWirelessApGroup.mockResolvedValue(detail(candidate))
    const wrapper = render()
    await flushPromises()
    const rows = wrapper.findAll('.wireless-group-row')
    expect(rows[0].text()).toContain('候选')
    expect(rows[0].text()).toContain('稳定身份待确认')
    expect(rows[1].text()).toContain('来源待确认')
    expect(rows[1].text()).toContain('未知')
    expect(wrapper.get('.wireless-groups').text()).not.toContain('AC|ac-a|同名组')
    await rows[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('.wireless-group-detail').text()).toContain('待确认，尚无可信来源键')
    expect(wrapper.get('.wireless-group-detail').text()).toContain('未提供')
  })

  it('retains a Domain group when filtering any of its source ACs', async () => {
    const shared = group('domain-group', 'ac-a', { groupScope: 'DOMAIN',
      groupIdentityKey: 'DOMAIN|domain-one|source-key', sourceAcDeviceIds: ['ac-a', 'ac-b'] })
    api.getWirelessApGroups.mockResolvedValue(page([group('local-a'), shared, group('local-b', 'ac-b')]))
    const wrapper = render()
    await flushPromises()
    await wrapper.findAll('.wireless-tree__member')[1].trigger('click')
    await flushPromises()
    const rows = wrapper.findAll('.wireless-group-row')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('Domain 来源 · domain-one')
    expect(rows[0].text()).toContain('DOMAIN|domain-one|source-key')
    expect(rows[1].text()).toContain('AC 来源 · ac-b')
    expect(rows.map(row => row.text()).join(' ')).not.toContain('AC|ac-a|source-key')
    expect(api.getWirelessApGroups).toHaveBeenLastCalledWith(domain.id, { page: 1, pageSize: 100, sourceAcId: 'ac-b' })
    const scoped = { ...shared, sourceAcDeviceId: 'ac-b', sourceAcDeviceIds: ['ac-b'], sourceObservationIds: ['observation-b'], sourceEvidenceRefs: ['synthetic-raw-b'] }
    api.getWirelessApGroup.mockResolvedValue(detail(scoped))
    await rows[0].trigger('click')
    await flushPromises()
    expect(api.getWirelessApGroup).toHaveBeenLastCalledWith(shared.id, { sourceAcId: 'ac-b' })
    expect(wrapper.get('.wireless-group-detail').text()).toContain('observation-b')
    expect(wrapper.get('.wireless-group-detail').text()).not.toContain('observation-domain-group')
  })

  it('displays source identity conflicts and their reason', async () => {
    const conflict = group('conflicting', 'ac-a', { groupIdentityStatus: 'CONFLICT', conflictReason: '来源键与组关系存在冲突' })
    api.getWirelessApGroups.mockResolvedValue(page([conflict]))
    api.getWirelessApGroup.mockResolvedValue(detail(conflict))
    const wrapper = render()
    await flushPromises()
    expect(wrapper.get('.wireless-group-row .state-pill').text()).toBe('冲突')
    await wrapper.get('.wireless-group-row').trigger('click')
    await flushPromises()
    expect(wrapper.get('.wireless-group-detail .bad-text').text()).toBe(conflict.conflictReason)
  })

  it('ignores delayed details from a previously selected same-name group', async () => {
    const first = group('projection-a'), second = group('projection-b', 'ac-b')
    let resolveFirst!: (value: WirelessApGroupDetail) => void
    const pending = new Promise<WirelessApGroupDetail>(resolve => { resolveFirst = resolve })
    api.getWirelessApGroups.mockResolvedValue(page([first, second]))
    api.getWirelessApGroup.mockImplementation((id: string) => id === first.id ? pending : Promise.resolve(detail(second)))
    const wrapper = render()
    await flushPromises()
    await wrapper.findAll('.wireless-group-row')[0].trigger('click')
    await wrapper.findAll('.wireless-group-row')[1].trigger('click')
    await flushPromises()
    resolveFirst(detail(first))
    await flushPromises()
    const provenance = wrapper.get('.wireless-group-detail').text()
    expect(provenance).toContain('membership-projection-b')
    expect(provenance).not.toContain('membership-projection-a')
    expect(wrapper.findAll('.wireless-group-row.selected')).toHaveLength(1)
    expect(wrapper.get('.wireless-group-row.selected').text()).toContain('AC|ac-b|source-key')
  })

  it('keeps the available domain visible when AP resources fail', async () => {
    api.getWirelessApResources.mockRejectedValue(new Error('AP 服务暂不可用'))
    const wrapper = render()
    await flushPromises()
    expect(wrapper.get('.wireless-domain-header').text()).toContain('Synthetic Domain')
    const alerts = wrapper.findAll('[role=alert]').map(item => item.text()).join(' ')
    expect(alerts).toContain('AP 资源读取失败')
    expect(alerts).toContain('已保留其余可用数据')
    expect(wrapper.find('.wireless-main').text()).not.toContain('无线业务暂不可用')
  })
})
