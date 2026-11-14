import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, ref, type Component } from 'vue'
import type { WirelessAcDomain, WirelessApGroup, WirelessApGroupDetail, WirelessApResource } from '../api/client'
import WirelessView from './WirelessView.vue'

const api = vi.hoisted(() => ({
  createWirelessTask: vi.fn(), getH3cTaskPlan: vi.fn(), getWirelessTasks: vi.fn(), getWirelessTask: vi.fn(), retryWirelessTask: vi.fn(), downloadWirelessTask: vi.fn(), cancelH3cTaskPlan: vi.fn(),
  getWirelessAcDomains: vi.fn(), getWirelessApGroup: vi.fn(), getWirelessApGroups: vi.fn(),
  getWirelessApResources: vi.fn(), getWirelessCollectionManifest: vi.fn(),
  getWirelessCollectionManifests: vi.fn(), getWirelessRuntimeState: vi.fn(),
  getWirelessIdentityReview: vi.fn(), getWirelessIdentityLifecycle: vi.fn(),
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
function render(component: Component = WirelessView) {
  return mount(component, { global: {
    directives: { loading: () => undefined },
    stubs: {
      WirelessOperationalCapabilities: true,
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
    api.getWirelessIdentityReview.mockResolvedValue({ resourceId: 'physical-ap', identityStatus: 'CONFIRMED', identityRevision: 'identity-revision', scopeStatus: 'FULL', canConfirm: false, canReject: false, decisionBlockReason: 'IDENTITY_ALREADY_CONFIRMED', conflictReason: null, evidenceRefs: ['raw:identity'] })
    api.getWirelessIdentityLifecycle.mockResolvedValue({ resourceId: 'physical-ap', state: 'PENDING_REVIEW', revision: 'lifecycle-revision', replacementResourceId: null, evidenceRefs: ['raw:identity'], reason: null, actor: null, decidedMs: null, sourceRevision: 'identity-revision', history: [] })
  })

  function observedResource(revision: string): WirelessApResource {
    return { id: 'physical-ap', displayName: 'Synthetic AP', identityStatus: 'CONFIRMED',
      normalizedMacSet: ['001122334455'], sourceObservationIds: [revision], sourceAcDomainIds: [domain.id],
      scopeStatus: 'FULL', firstSeenMs: domain.asOf, lastSeenMs: domain.asOf, sourceRevision: revision, projectionRevision: revision,
      observations: [{ id: revision, resourceId: 'physical-ap', sourceAcDeviceId: 'ac-a', sourceAcDomainId: domain.id,
        sourceSiteId: 'site-one', observedApName: 'Synthetic AP', normalizedMacSet: ['001122334455'],
        rawState: 'R/M', normalizedState: 'RUNNING_MASTER', stateConfidence: 'HIGH', capturedMs: domain.asOf,
        receivedMs: domain.asOf, rawManifestRef: revision, parserRevision: 'synthetic', sourceRevision: revision, relationStatus: 'OBSERVED' }] }
  }

  it('exports loaded observations through the normal AP toolbar', async () => {
    api.getWirelessApResources.mockResolvedValue(page([observedResource('export-raw-ref')]))
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:wireless-export')
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    try {
      const wrapper = render()
      await flushPromises()
      const button = wrapper.findAll('button').find(item => item.text() === '导出当前 AP 观察 CSV')!
      await button.trigger('click')
      expect(create).toHaveBeenCalledOnce()
      expect(click).toHaveBeenCalledOnce()
      expect(revoke).toHaveBeenCalledWith('blob:wireless-export')
      wrapper.unmount()
    } finally { create.mockRestore(); revoke.mockRestore(); click.mockRestore() }
  })

  it('shows the selected AP runtime projection instead of an AC member state', async () => {
    const runningBackupDomain = { ...domain, members: domain.members.map(member => ({ ...member, runtimeState: 'RUNNING_BACKUP' })) }
    api.getWirelessAcDomains.mockResolvedValue(page([runningBackupDomain]))
    api.getWirelessApResources.mockResolvedValue(page([observedResource('ap-master-evidence')]))
    api.getWirelessRuntimeState.mockResolvedValue({
      resourceId: 'physical-ap', normalizedState: 'RUNNING_MASTER', sources: [],
      scope: { status: 'FULL', claimsRevision: 'synthetic-claims' }, asOf: domain.asOf,
      sourceRevision: 'ap-runtime-revision', freshnessWindowMs: 300_000, warnings: [],
      current: null, previousEffective: null, recent10: [],
    })

    const wrapper = render()
    await flushPromises()

    expect(wrapper.findAll('.wireless-stats .stat-card')[2].find('.stat-card__value').text()).toBe('运行主')
    expect(wrapper.find('.wireless-members').text()).toContain('运行备')
    wrapper.unmount()
  })

  it('shows identity review evidence and lifecycle state without inventing a decision', async () => {
    api.getWirelessApResources.mockResolvedValue(page([observedResource('identity-ref')]));
    api.getWirelessIdentityReview.mockResolvedValue({ resourceId: 'physical-ap', identityStatus: 'CANDIDATE', identityRevision: 'identity-revision', scopeStatus: 'FULL', canConfirm: false, canReject: true, decisionBlockReason: 'INCOMPLETE_EXACT_EVIDENCE', conflictReason: null, evidenceRefs: ['fact:serial', 'fact:mac'] })
    api.getWirelessIdentityLifecycle.mockResolvedValue({ resourceId: 'physical-ap', state: 'PENDING_REVIEW', revision: 'lifecycle-revision', replacementResourceId: null, evidenceRefs: ['fact:serial', 'fact:mac'], reason: null, actor: null, decidedMs: null, sourceRevision: 'identity-revision', history: [] })

    const wrapper = render()
    await flushPromises()

    const card = wrapper.get('.wireless-identity')
    expect(card.text()).toContain('AP 身份核验')
    expect(card.text()).toContain('候选')
    expect(card.text()).toContain('待核验')
    expect(card.text()).toContain('fact:serial / fact:mac')
    expect(card.text()).toContain('INCOMPLETE_EXACT_EVIDENCE')
    expect(api.getWirelessIdentityReview).toHaveBeenCalledWith('physical-ap')
    expect(api.getWirelessIdentityLifecycle).toHaveBeenCalledWith('physical-ap')
  })

  it('exports loaded effective history and blocks export after its refresh fails', async () => {
    api.getWirelessApResources.mockResolvedValue(page([observedResource('history-ref')]))
    api.getWirelessRuntimeState.mockResolvedValue({
      resourceId: 'physical-ap', normalizedState: 'STALE', sources: [],
      scope: { status: 'PARTIAL', claimsRevision: 'claims' }, asOf: domain.asOf,
      sourceRevision: 'history-revision', freshnessWindowMs: 300_000, warnings: [],
      current: null, previousEffective: null, recent10: [],
    })
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:history')
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    try {
      const wrapper = render(); await flushPromises()
      const exportButton = () => wrapper.findAll('button').find(item => item.text() === '导出 AP 状态历史 CSV')!
      expect(exportButton().attributes('disabled')).toBeUndefined()
      await exportButton().trigger('click')
      expect(create).toHaveBeenCalledOnce(); expect(click).toHaveBeenCalledOnce()
      expect(revoke).toHaveBeenCalledWith('blob:history')
      api.getWirelessRuntimeState.mockRejectedValue(new Error('history claims changed'))
      await wrapper.get('.wireless-tree__member').trigger('click'); await flushPromises()
      expect(exportButton().attributes('disabled')).toBeDefined()
      await exportButton().trigger('click')
      expect(create).toHaveBeenCalledOnce()
      expect(wrapper.text()).toContain('状态历史读取失败')
    } finally { create.mockRestore(); revoke.mockRestore(); click.mockRestore() }
  })

  it('keeps history export disabled for a late response belonging to a different AP', async () => {
    api.getWirelessApResources.mockResolvedValue(page([observedResource('history-ref')]))
    api.getWirelessRuntimeState.mockResolvedValue({
      resourceId: 'other-ap', normalizedState: 'RUNNING_MASTER', sources: [],
      scope: { status: 'FULL', claimsRevision: 'claims' }, asOf: domain.asOf,
      sourceRevision: 'old-history', freshnessWindowMs: 300_000, warnings: [],
      current: null, previousEffective: null, recent10: [],
    })
    const wrapper = render(); await flushPromises()
    const button = wrapper.findAll('button').find(item => item.text() === '导出 AP 状态历史 CSV')!
    expect(button.attributes('disabled')).toBeDefined()
  })

  it('clears prior observations when a refreshed resource query fails', async () => {
    api.getWirelessApResources.mockResolvedValue(page([observedResource('old-claim-ref')]))
    const wrapper = render()
    await flushPromises()
    expect(wrapper.get('.wireless-observation').text()).toContain('采集证据')
    expect(wrapper.get('.wireless-observation > div').text()).not.toContain('old-claim-ref')
    expect(wrapper.get('.wireless-observation__evidence').text()).toContain('old-claim-ref')
    api.getWirelessApResources.mockRejectedValue(new Error('claims changed'))
    const refresh = wrapper.findAll('button').find(item => item.text() === '刷新数据')!
    await refresh.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('old-claim-ref')
    expect(wrapper.text()).toContain('AP 资源读取失败')
    wrapper.unmount()
  })

  it('reloads Observation and collection evidence when a running task finishes', async () => {
    vi.useFakeTimers()
    try {
      const summary = { id: 'freshness-task', state: 'RUNNING', createdMs: domain.asOf, updatedMs: domain.asOf, total: 1, succeeded: 0, failed: 0 }
      api.getWirelessTasks.mockResolvedValue([summary])
      api.getWirelessTask.mockResolvedValue({ summary, items: [] })
      api.getWirelessApResources.mockResolvedValue(page([observedResource('old-raw-ref')]))
      const wrapper = render()
      await flushPromises()
      expect(wrapper.get('.wireless-observation__evidence').text()).toContain('old-raw-ref')
      await wrapper.get('.wireless-task-row button').trigger('click')
      await flushPromises()
      api.getWirelessApResources.mockResolvedValue(page([observedResource('published-raw-ref')]))
      api.getWirelessTask.mockResolvedValue({ summary: { ...summary, state: 'SUCCEEDED', succeeded: 1, updatedMs: domain.asOf + 1 }, items: [] })
      const collectionsBefore = api.getWirelessCollectionManifests.mock.calls.length
      await vi.advanceTimersByTimeAsync(3000)
      await flushPromises()
      expect(wrapper.get('.wireless-observation__evidence').text()).toContain('published-raw-ref')
      expect(wrapper.get('.wireless-observation__evidence').text()).not.toContain('old-raw-ref')
      expect(api.getWirelessCollectionManifests.mock.calls.length).toBeGreaterThan(collectionsBefore)
      const reads = api.getWirelessApResources.mock.calls.length
      await wrapper.get('.wireless-task-detail button').trigger('click')
      await flushPromises()
      expect(api.getWirelessApResources).toHaveBeenCalledTimes(reads)
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('reloads cached Observation projections when returning to the wireless tab', async () => {
    const shown = ref(true)
    const host = defineComponent({ components: { WirelessView }, setup: () => ({ shown }), template: '<KeepAlive><WirelessView v-if="shown" /></KeepAlive>' })
    api.getWirelessApResources.mockResolvedValue(page([observedResource('before-navigation')]))
    const wrapper = render(host)
    await flushPromises()
    expect(api.getWirelessApResources).toHaveBeenCalledTimes(1)
    shown.value = false
    await flushPromises()
    api.getWirelessApResources.mockResolvedValue(page([observedResource('after-navigation')]))
    shown.value = true
    await flushPromises()
    expect(wrapper.get('.wireless-observation__evidence').text()).toContain('after-navigation')
    expect(wrapper.get('.wireless-observation__evidence').text()).not.toContain('before-navigation')
    expect(api.getWirelessApResources).toHaveBeenCalledTimes(2)
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
    expect(api.createWirelessTask).toHaveBeenCalledWith(['domain-one'], ['display version', 'display wlan ap all', 'display wlan ap all address'], undefined)
    expect(wrapper.get('.wireless-task-result').text()).toContain('QUEUED')
    await wrapper.findAll('.wireless-task button')[1].trigger('click')
    await flushPromises()
    expect(api.getH3cTaskPlan).toHaveBeenCalledWith('plan-one')
    expect(wrapper.get('.wireless-task-result').text()).toContain('RUNNING')
  })

  it('starts the standard read-only collection directly for the selected AC', async () => {
    const plan = { id: 'ac-refresh', state: 'QUEUED', items: [{ ordinal: 0, target: 'ac-b', state: 'QUEUED' }] }
    const summary = { id: plan.id, state: 'QUEUED', createdMs: domain.asOf, updatedMs: domain.asOf, total: 1, succeeded: 0, failed: 0 }
    api.createWirelessTask.mockResolvedValue({ id: plan.id, plan })
    api.getWirelessTasks.mockResolvedValue([summary])
    api.getWirelessTask.mockResolvedValue({ summary, items: [] })
    const wrapper = render()
    await flushPromises()

    await wrapper.findAll('.wireless-tree__member')[1].trigger('click')
    const quickCollect = wrapper.get('.wireless-task-quick-collect')
    expect(quickCollect.text()).toContain('采集当前 AC')
    await quickCollect.trigger('click')
    await flushPromises()

    expect(api.createWirelessTask).toHaveBeenCalledWith(
      ['domain-one'],
      ['display version', 'display wlan ap all', 'display wlan ap all address'],
      ['ac-b'],
    )
    expect(wrapper.get('.wireless-task-result').text()).toContain('QUEUED')
    expect(wrapper.get('.wireless-task-detail').text()).toContain('ac-refresh')
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

  it('combines frozen Site, AC and state filters without inferring old scope from current domains', async () => {
    const base = { state: 'FAILED', createdMs: domain.asOf, updatedMs: domain.asOf, total: 1, succeeded: 0, failed: 1 }
    api.getWirelessTasks.mockResolvedValue([
      { ...base, id: 'old-site-ac-a', siteIds: ['retired-site'], acDeviceIds: ['ac-a'] },
      { ...base, id: 'new-site-ac-a', siteIds: ['site-one'], acDeviceIds: ['ac-a'] },
      { ...base, id: 'old-site-ac-b', siteIds: ['retired-site'], acDeviceIds: ['ac-b'] },
      { ...base, id: 'succeeded', state: 'SUCCEEDED', siteIds: ['retired-site'], acDeviceIds: ['ac-a'] },
      { ...base, id: 'legacy-no-scope' },
    ])
    const wrapper = render()
    await flushPromises()
    const ids = () => wrapper.findAll('.wireless-task-row').map(row => row.attributes('data-task-id'))
    expect(ids()).toHaveLength(5)
    await wrapper.get('[aria-label="任务历史 Site"]').setValue('retired-site')
    expect(ids()).toEqual(['old-site-ac-a', 'old-site-ac-b', 'succeeded'])
    await wrapper.get('[aria-label="任务历史 AC"]').setValue('ac-a')
    expect(ids()).toEqual(['old-site-ac-a', 'succeeded'])
    await wrapper.get('[aria-label="任务历史状态"]').setValue('FAILED')
    expect(ids()).toEqual(['old-site-ac-a'])
    expect(wrapper.get('.wireless-history-count').text()).toContain('1 / 5')
    await wrapper.get('[aria-label="任务历史状态"]').setValue('CANCELLED')
    expect(ids()).toEqual([])
    expect(wrapper.get('.wireless-task-history .empty-state').text()).toContain('没有符合筛选条件')
    await wrapper.findAll('.wireless-history-filters button')[0].trigger('click')
    expect(ids()).toHaveLength(5)
    expect(api.getWirelessTasks).toHaveBeenCalledTimes(1)
  })

  it('invalidates a pending task report when a history filter excludes it', async () => {
    const summary = { id: 'pending-history', state: 'FAILED', createdMs: domain.asOf, updatedMs: domain.asOf, total: 1, succeeded: 0, failed: 1, siteIds: ['site-one'], acDeviceIds: ['ac-a'] }
    api.getWirelessTasks.mockResolvedValue([summary])
    let resolve!: (value: unknown) => void
    api.getWirelessTask.mockReturnValue(new Promise(done => { resolve = done }))
    const wrapper = render()
    await flushPromises()
    await wrapper.get('.wireless-task-row button').trigger('click')
    await wrapper.get('[aria-label="任务历史状态"]').setValue('SUCCEEDED')
    resolve({ summary, items: [] })
    await flushPromises()
    expect(wrapper.find('.wireless-task-detail').exists()).toBe(false)
    expect(wrapper.findAll('.wireless-task-row')).toHaveLength(0)
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

  function failedReport() {
    const command = (name: string, state: string) => ({ command: name, state, error: state === 'FAILED' ? 'TIMEOUT' : null, captureStatus: null, parseStatus: null, capturedMs: null })
    return {
      summary: { id: 'retry-plan', state: 'FAILED', createdMs: domain.asOf, updatedMs: domain.asOf, total: 2, succeeded: 1, failed: 1 },
      items: [
        { ordinal: 0, deviceId: 'ac-a', state: 'SUCCEEDED', runId: 'run-a', error: null, captureStatus: null, parseStatus: null, commands: [command('display version', 'SUCCEEDED')] },
        { ordinal: 1, deviceId: 'ac-b', state: 'FAILED', runId: 'run-b', error: 'TIMEOUT', captureStatus: null, parseStatus: null, commands: [command('display version', 'SUCCEEDED'), command('display wlan ap all', 'FAILED')] },
      ], retries: [],
    }
  }
  async function openFailedReport() {
    const report = failedReport()
    api.getWirelessTasks.mockResolvedValue([report.summary])
    api.getWirelessTask.mockResolvedValue(report)
    const wrapper = render()
    await flushPromises()
    await wrapper.get('.wireless-task-row button').trigger('click')
    await flushPromises()
    return { wrapper, report }
  }

  it('offers retry only on failed AC and defaults to its failed commands', async () => {
    const { wrapper, report } = await openFailedReport()
    api.retryWirelessTask.mockResolvedValue({ ...report, retries: [{ ordinal: 1, deviceId: 'ac-b', originalRunId: 'run-b', retryRunId: 'retry-b', state: 'SUCCEEDED', createdMs: domain.asOf, commandIndexes: [1], commands: report.items[1].commands.slice(1) }] })
    expect(wrapper.findAll('.wireless-task-ac')[0].find('button').exists()).toBe(false)
    const buttons = wrapper.findAll('.wireless-task-ac')[1].findAll('button')
    expect(buttons).toHaveLength(2)
    await buttons[0].trigger('click')
    await flushPromises()
    expect(api.retryWirelessTask).toHaveBeenCalledWith('retry-plan', 1, undefined)
    expect(wrapper.get('.wireless-retry-history').text()).toContain('1 次')
    expect(wrapper.get('.wireless-retry-history').text()).toContain('retry-b')
  })

  it('submits the original failed command index and blocks duplicate retry requests', async () => {
    const { wrapper, report } = await openFailedReport()
    let finish!: (value: unknown) => void
    api.retryWirelessTask.mockReturnValue(new Promise(resolve => { finish = resolve }))
    const button = wrapper.findAll('.wireless-task-ac')[1].findAll('button')[1]
    await button.trigger('click')
    await button.trigger('click')
    expect(api.retryWirelessTask).toHaveBeenCalledTimes(1)
    expect(api.retryWirelessTask).toHaveBeenCalledWith('retry-plan', 1, [1])
    finish({ ...report, retries: [{ ordinal: 1, deviceId: 'ac-b', originalRunId: 'run-b', retryRunId: 'retry-b', state: 'RUNNING', createdMs: domain.asOf, commandIndexes: [1], commands: [] }] })
    await flushPromises()
    expect(button.attributes('disabled')).toBeDefined()
  })

  it('keeps original detail after retry or detail refresh failure', async () => {
    const { wrapper } = await openFailedReport()
    api.retryWirelessTask.mockRejectedValue(new Error('重试队列已满'))
    await wrapper.findAll('.wireless-task-ac')[1].findAll('button')[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('.wireless-task-detail').text()).toContain('retry-plan')
    expect(wrapper.get('.wireless-task-detail [role=alert]').text()).toContain('重试队列已满')
    expect(wrapper.get('.wireless-task-detail').text()).toContain('ac-b')
    api.getWirelessTask.mockRejectedValue(new Error('刷新暂不可用'))
    await wrapper.get('.wireless-task-detail button').trigger('click')
    await flushPromises()
    expect(wrapper.get('.wireless-task-detail').text()).toContain('ac-b')
  })

  it('polls running retries even when the original task has finished', async () => {
    vi.useFakeTimers()
    try {
      const { wrapper, report } = await openFailedReport()
      const retry = { ordinal: 1, deviceId: 'ac-b', originalRunId: 'run-b', retryRunId: 'retry-b', state: 'RUNNING', createdMs: domain.asOf, commandIndexes: [1], commands: [] }
      api.retryWirelessTask.mockResolvedValue({ ...report, retries: [retry] })
      await wrapper.findAll('.wireless-task-ac')[1].findAll('button')[0].trigger('click')
      await flushPromises()
      api.getWirelessTask.mockResolvedValue({ ...report, retries: [{ ...retry, state: 'SUCCEEDED' }] })
      await vi.advanceTimersByTimeAsync(3000)
      await flushPromises()
      expect(wrapper.get('.wireless-retry-history').text()).toContain('SUCCEEDED')
      expect(wrapper.findAll('.wireless-task-ac')[1].get('button').attributes('disabled')).toBeUndefined()
      wrapper.unmount()
    } finally { vi.useRealTimers() }
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
