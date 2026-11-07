import { describe, expect, it, vi } from 'vitest'
import { downloadTaskHistory, executeConfigRestore, getConfigRestoreStatus, getDeviceInventory, getHealth, getTask, getWirelessApGroup, getWirelessApGroups, previewConfigRestore, setCsrfToken } from './client'

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

  it('keeps the selected AC scope on group lists and details', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }))
    try {
      await getWirelessApGroups('domain/a', { sourceAcId: 'ac/b' })
      await getWirelessApGroup('projection/a', { sourceAcId: 'ac/b' })
      expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
        '/api/system/wireless/ac-domains/domain%2Fa/ap-groups?sourceAcId=ac%2Fb',
        '/api/system/wireless/ap-groups/projection%2Fa?sourceAcId=ac%2Fb',
      ])
    } finally { fetchMock.mockRestore() }
  })

  it('looks up a task by id through the scoped endpoint without exposing credentials', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ id: 'run/1', state: 'FAILED' }), { status: 200, headers: { 'content-type': 'application/json' } }))
    try {
      await getTask('run/1')
      expect(fetchMock.mock.calls[0][0]).toBe('/api/system/tasks/run%2F1')
      expect(String(fetchMock.mock.calls[0][0])).not.toContain('password')
    } finally { fetchMock.mockRestore() }
  })

  it('downloads task history through the scoped CSV endpoint', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('csv-fixture', { status: 200, headers: { 'content-type': 'text/csv' } }))
    try {
      const blob = await downloadTaskHistory()
      expect(await blob.text()).toBe('csv-fixture')
      expect(fetchMock).toHaveBeenCalledWith('/api/system/tasks/export', { credentials: 'include' })
    } finally { fetchMock.mockRestore() }
  })

  it('keeps configuration restore confirmation on POST bodies and out of URLs', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response(JSON.stringify({ id: 'preview-1' }), { status: 200, headers: { 'content-type': 'application/json' } }))
    try {
      await previewConfigRestore({ snapshotId: 'snapshot/1', targetProfileId: 'profile/1' })
      await executeConfigRestore({ previewId: 'preview/1', confirmationToken: 'token-fixture', apply: false })
      expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
        '/api/system/config-backups/restore/preview',
        '/api/system/config-backups/restore/execute',
      ])
      expect(String(fetchMock.mock.calls[0][0])).not.toContain('snapshot')
      expect(String(fetchMock.mock.calls[1][0])).not.toContain('token')
      expect(JSON.parse(String((fetchMock.mock.calls[1][1] as RequestInit).body))).toEqual({ previewId: 'preview/1', confirmationToken: 'token-fixture', apply: false })
    } finally { fetchMock.mockRestore() }
  })

  it('reads restore status through an encoded, read-only operation endpoint', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ id: 'restore/1', state: 'REQUIRES_REVIEW' }), { status: 200, headers: { 'content-type': 'application/json' } }))
    try {
      await getConfigRestoreStatus('restore/1')
      expect(fetchMock.mock.calls[0][0]).toBe('/api/system/config-backups/restore/operations/restore%2F1')
    } finally { fetchMock.mockRestore() }
  })
})

