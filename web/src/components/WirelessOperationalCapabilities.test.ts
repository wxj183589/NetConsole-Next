import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import WirelessOperationalCapabilities from './WirelessOperationalCapabilities.vue'
const api = vi.hoisted(() => ({ getWirelessOperationalCapabilities: vi.fn() }))
vi.mock('../api/client', () => api)
const wrappers: ReturnType<typeof mount>[] = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); vi.resetAllMocks() })
function render() { const wrapper = mount(WirelessOperationalCapabilities, { props: { domainId: 'domain', resourceId: 'ap' } }); wrappers.push(wrapper); return wrapper }
describe('wireless capability evidence', () => {
  it('distinguishes observed roles, untested failover and unknown direct AP access', async () => {
    api.getWirelessOperationalCapabilities.mockResolvedValue({ asOf: 1000, sourceRevision: 'revision-1',
      domains: [{ domainId: 'domain', domainKind: 'AC_DOMAIN', topologyEvidence: 'USER_CONFIRMED_TOPOLOGY', deviceRoleEvidence: 'DEVICE_REPORTED_ROLE', haVerification: 'NOT_TESTED', reason: 'NO_CONTROLLED_SWITCH', observations: [{ rawState: 'R/M' }] }],
      fitAps: [{ resourceId: 'ap', displayName: 'AP1', identityStatus: 'CONFIRMED', acManagedState: 'AC_OBSERVATION_AVAILABLE', directAccessState: 'UNKNOWN', acDeviceIds: ['ac1'], apGroups: [], observations: [] }] })
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('USER_CONFIRMED_TOPOLOGY'); expect(wrapper.text()).toContain('NOT_TESTED')
    expect(wrapper.text()).toContain('直接登录 UNKNOWN'); expect(wrapper.text()).toContain('AP Group：UNKNOWN')
    expect(wrapper.text()).not.toContain('DEVICE_VERIFIED_HA')
  })
  it('clears old evidence while retrying and exposes a failed read', async () => {
    api.getWirelessOperationalCapabilities.mockResolvedValue({ asOf: 1000, sourceRevision: 'old-revision', domains: [], fitAps: [] })
    const wrapper = render(); await flushPromises(); expect(wrapper.text()).toContain('old-revision')
    api.getWirelessOperationalCapabilities.mockRejectedValue(new Error('scope revoked'))
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('scope revoked'); expect(wrapper.text()).not.toContain('old-revision')
  })
  it('shows independent AC roles with freshness and traceable evidence', async () => {
    const observations = [
      { observationId: 'one', acDeviceId: 'ac1', rawState: 'R/M', normalizedState: 'RUNNING_MASTER', capturedMs: 999_999, rawManifestRef: 'raw-one', sourceRevision: 'rev-one' },
      { observationId: 'two', acDeviceId: 'ac2', rawState: 'R/B', normalizedState: 'RUNNING_BACKUP', capturedMs: 699_999, rawManifestRef: 'raw-two', sourceRevision: 'rev-two' },
    ]
    api.getWirelessOperationalCapabilities.mockResolvedValue({ asOf: 1_000_000, sourceRevision: 'revision', domains: [
      { domainId: 'domain', domainKind: 'UNKNOWN', topologyEvidence: 'CANDIDATE', topologyEvidenceRefs: ['topology-ref'], deviceRoleEvidence: 'DEVICE_REPORTED_ROLE', haVerification: 'NOT_TESTED', reason: 'NO_SWITCH', observations },
    ], fitAps: [] })
    const wrapper = render(); await flushPromises()
    expect(wrapper.text()).toContain('ac1 · R/M → RUNNING_MASTER · RECENT')
    expect(wrapper.text()).toContain('ac2 · R/B → RUNNING_BACKUP · STALE')
    expect(wrapper.text()).toContain('raw-two'); expect(wrapper.text()).toContain('rev-two')
    expect(wrapper.text()).toContain('topology-ref')
    expect(wrapper.text()).toContain('当前范围暂无所选 AP')
    expect(wrapper.text()).toContain('NOT_TESTED')
  })
  it('invalidates an outstanding read when the selected resource changes', async () => {
    let finishOld!: (value: unknown) => void
    api.getWirelessOperationalCapabilities.mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
      .mockResolvedValueOnce({ asOf: 2000, sourceRevision: 'new-revision', domains: [], fitAps: [] })
    const wrapper = render()
    await wrapper.setProps({ resourceId: 'new-ap' }); await flushPromises()
    finishOld({ asOf: 1000, sourceRevision: 'forbidden-old-revision', domains: [], fitAps: [] }); await flushPromises()
    expect(wrapper.text()).toContain('new-revision')
    expect(wrapper.text()).not.toContain('forbidden-old-revision')
  })
})
