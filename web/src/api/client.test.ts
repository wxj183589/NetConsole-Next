import { describe, expect, it, vi } from 'vitest'
import { getHealth, setCsrfToken } from './client'

describe('Next API client', () => {
  it('uses same-origin credentials and never places credentials in a URL', async () => {
    setCsrfToken('csrf-fixture')
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ status: 'HEALTHY' }), { status: 200, headers: { 'content-type': 'application/json' } }))
    await getHealth()
    expect(fetchMock).toHaveBeenCalledWith('/api/system/health', expect.objectContaining({ credentials: 'include' }))
    expect(String(fetchMock.mock.calls[0][0])).not.toContain('password')
    fetchMock.mockRestore()
  })
})

