import { describe, expect, it, vi } from 'vitest'
import { getDeviceInventory, getHealth, setCsrfToken } from './client'

describe('Next API client', () => {
  it('uses same-origin credentials and never places credentials in a URL', async () => {
    setCsrfToken('csrf-fixture')
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ status: 'HEALTHY' }), { status: 200, headers: { 'content-type': 'application/json' } }))
    await getHealth()
    expect(fetchMock).toHaveBeenCalledWith('/api/system/health', expect.objectContaining({ credentials: 'include' }))
    expect(String(fetchMock.mock.calls[0][0])).not.toContain('password')
    fetchMock.mockRestore()
  })

  it('keeps device inventory filtering server-side and scoped', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ page: 1, pageSize: 20, total: 0, hasMore: false, summary: {}, items: [] }), { status: 200, headers: { 'content-type': 'application/json' } }))
    await getDeviceInventory({ siteId: 'local', groupId: 'group-core', vendor: 'H3C', deviceType: 'SWITCH' })
    const url = String(fetchMock.mock.calls[0][0])
    expect(url).toContain('siteId=local')
    expect(url).toContain('groupId=group-core')
    expect(url).toContain('vendor=H3C')
    expect(url).toContain('deviceType=SWITCH')
    expect(url).not.toContain('password')
    fetchMock.mockRestore()
  })
})

