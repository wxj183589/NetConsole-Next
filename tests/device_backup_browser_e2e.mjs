// Chrome + source Vite UI acceptance with mocked API responses; no device or database writes.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'device-backup-browser')
fs.mkdirSync(output, { recursive: true })
const server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: 0 } })
let browser
let page
const errors = []; const unexpected = []
try {
  await server.listen()
  const base = `http://127.0.0.1:${server.httpServer.address().port}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
  const posts = []; const reads = []; const evidence = []
  let mode = 'normal'; let role = 'SUPERADMIN'
  const profiles = ['a', 'b'].map(id => ({ id: `profile-${id}`, host: '192.0.2.10', port: 22, username: 'reader', enabled: true, supportedProtocols: ['SSH'], terminalProtocol: 'SSH', hostKeyUpdatePolicy: 'AUTO_UPDATE', terminalEncoding: 'AUTO' }))
  const devices = profiles.map((profile, index) => ({ id: `device-${index ? 'b' : 'a'}`, deviceId: `device-${index ? 'b' : 'a'}`, name: index ? '交换机乙' : '交换机甲', managementAddress: profile.host, managementIp: profile.host, vendor: 'H3C', model: 'fixture', deviceType: 'SWITCH', protocol: 'SSH', site: '测试局点', siteId: 'local', group: '未分组', enabled: true, credentialConfigured: true, hostKeyUpdatePolicy: 'AUTO_UPDATE', terminalEncoding: 'AUTO', terminalProfileId: profile.id, connectionStatus: 'UNKNOWN', isValid: true, qualityIssues: [] }))
  const summary = { totalRecords: 2, validDevices: 2, invalidRecords: 0, enabledDevices: 2, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 2 }
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
    if (!endpoint.startsWith('/api/')) return route.continue()
    let status = 200; let body
    if (endpoint === '/api/auth/me') body = { username: 'fixture-reader', role }
    else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-fixture-csrf' }
    else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'fixture' }
    else if (endpoint === '/api/system/modules') body = [{ key: 'devices', enabled: true }, { key: 'config-backups', enabled: mode !== 'disabled' }]
    else if (endpoint === '/api/system/devices') body = { items: devices, total: 2, page: 1, pageSize: 20, hasMore: false, summary }
    else if (endpoint === '/api/system/device-sites') body = [{ id: 'local', name: '测试局点' }]
    else if (endpoint === '/api/system/device-groups') body = []
    else if (endpoint === '/api/system/terminal/devices' || endpoint === '/api/system/h3c/profiles') body = profiles
    else if (endpoint.startsWith('/api/system/devices/')) {
      const device = devices.find(item => item.id === decodeURIComponent(endpoint.split('/').at(-1)))
      if (!device || mode === 'denied') { status = 404; body = {} }
      else body = { item: { ...device, vendor: mode === 'unsupported' ? 'Huawei' : 'H3C' }, description: '', createdMs: 1000, updatedMs: 1000, terminalProfileId: device.terminalProfileId, terminalProfile: profiles.find(profile => profile.id === device.terminalProfileId), recentSessions: [], auditEvents: [] }
    } else if (endpoint === '/api/system/config-backups' && request.method() === 'POST') {
      posts.push(request.postDataJSON())
      body = { id: 'fixture-batch', state: 'SUCCEEDED', succeeded: 1, total: 1 }
    } else if (endpoint === '/api/system/config-backups') {
      const id = url.searchParams.get('profileId'); reads.push(id)
      body = [2, 1].map(version => ({ id: `${id}-v${version}`, profileId: id, version, capturedMs: 1000, byteCount: 1024, sha256: '0123456789abcdef'.repeat(4) }))
    } else if (endpoint.includes('/diff/')) body = { addedLines: 1, removedLines: 0, added: ['当前设备配置差异'], removed: [] }
    else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  })

  await page.goto(`${base}/devices`)
  await expect(page.getByRole('heading', { name: '设备管理', exact: true })).toBeVisible()
  const row = page.locator('tbody tr').filter({ hasText: '交换机乙' })
  await row.getByRole('button', { name: '详情', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '设备详情 / 审计' })
  await dialog.getByRole('button', { name: '查看配置备份', exact: true }).click()
  await expect(page).toHaveURL(`${base}/config-backups?deviceId=device-b`)
  await expect(page.getByRole('heading', { name: '交换机乙 · 192.0.2.10 · 版本历史', exact: true })).toBeVisible()
  await expect(page.locator('tbody tr')).toHaveCount(2)
  expect(reads).toEqual(['profile-b']); expect(posts).toEqual([])
  await page.getByRole('button', { name: '比较', exact: true }).click()
  await expect(page.locator('.diff-panel pre').filter({ hasText: '当前设备配置差异' })).toBeVisible()
  await page.getByRole('button', { name: '开始配置备份', exact: true }).click()
  await expect(page.getByText('配置备份完成', { exact: true })).toBeVisible()
  expect(posts).toEqual([{ profileIds: ['profile-b'] }])
  await page.screenshot({ path: path.join(output, 'device-b-config-backup.png'), fullPage: true })
  evidence.push('device-detail-to-exact-backup-and-explicit-start')

  for (const [nextMode, query, expected] of [
    ['normal', 'missing-device', '设备不存在或不在当前授权范围内'],
    ['unsupported', 'device-b', '当前仅支持独立管理的 H3C 设备配置备份'],
    ['disabled', 'device-b', '配置备份模块未启用'],
  ]) {
    mode = nextMode
    const before = reads.length
    await page.goto(`${base}/config-backups?deviceId=${query}`)
    await expect(page.getByRole('status').filter({ hasText: expected })).toBeVisible()
    await expect(page.getByRole('button', { name: '开始配置备份', exact: true })).toHaveCount(0)
    expect(reads.length).toBe(before)
    evidence.push(`${nextMode}-no-first-profile-fallback`)
  }

  mode = 'normal'; role = 'ADMIN'
  await page.goto(`${base}/config-backups?deviceId=device-b`)
  await expect(page.getByRole('heading', { name: '交换机乙 · 192.0.2.10 · 版本历史', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '开始配置备份', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: '比较', exact: true }).click()
  await expect(page.locator('.diff-panel pre').filter({ hasText: '当前设备配置差异' })).toBeVisible()
  evidence.push('admin-authorized-history-and-diff-read-only')
  mode = 'denied'
  await page.locator('main .page-scroll').getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: '设备不存在或不在当前授权范围内' })).toBeVisible()
  await expect(page.locator('tbody')).toHaveCount(0)
  evidence.push('scope-revocation-clears-history-and-actions')
  expect(posts).toEqual([{ profileIds: ['profile-b'] }])
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  const report = { status: 'PASS', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING', viewport: '1920x1080 DPR1', scenarios: evidence, errors, unexpected }
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(report, null, 2))
  console.log(JSON.stringify(report))
} catch (error) {
  if (page) {
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true })
    console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: await page.locator('body').innerText() }))
  }
  throw error
} finally {
  await browser?.close()
  await server.close()
}
