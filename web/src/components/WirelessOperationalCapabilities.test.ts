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
})
