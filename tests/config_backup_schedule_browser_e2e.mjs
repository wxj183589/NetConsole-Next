// Owned Chrome + source Vite, synthetic API persistence; no service or device calls.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'config-backup-schedule-browser')
fs.mkdirSync(output, { recursive: true })
const scenarios = []; const screenshots = []; const requests = []; const errors = []; const unexpected = []
const viewports = [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]
const report = { status: 'RUNNING', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING',
  backendExercised: false, deviceConnections: 0, databaseWrites: 0, persistenceEvidence: 'MOCK_API_STATE_ONLY',
  startedAt: new Date().toISOString(), viewports, deviceScaleFactor: 1, scenarios, screenshots, requests, errors, unexpected,
  resources: { vitePort: null, ownedViteClosed: false, ownedChromeClosed: false } }
let server; let browser; let page; let failure
let role = 'SUPERADMIN'; let mode = 'normal'; let serial = 0
const schedules = new Map()
const profiles = ['a', 'b', 'c'].map((letter, index) => ({ id: `profile-${letter}`, host: `192.0.2.${10 + index}`, port: 22,
  username: 'fixture-reader', enabled: true, supportedProtocols: ['SSH'], terminalProtocol: 'SSH',
  hostKeyUpdatePolicy: 'AUTO_UPDATE', terminalEncoding: 'AUTO' }))
const devices = profiles.map((profile, index) => ({ id: `device-${'abc'[index]}`, deviceId: `device-${'abc'[index]}`,
  name: `交换机${'甲乙丙'[index]} · 合成样本`, managementIp: profile.host, managementAddress: profile.host,
  vendor: 'H3C', model: 'fixture', deviceType: 'SWITCH', protocol: 'SSH', siteId: 'fixture-site', site: '合成测试局点',
  group: '未分组', enabled: true, credentialConfigured: true, terminalProfileId: profile.id, isValid: true,
  connectionStatus: 'UNKNOWN', qualityIssues: [] }))
const mutations = () => requests.filter(request => request.method !== 'GET')
const creates = () => requests.filter(request => request.endpoint === '/api/system/config-backup-schedules' && request.method === 'POST')
const schedulePanel = () => page.getByRole('region', { name: '定时配置备份', exact: true })
const scheduleRows = () => schedulePanel().locator('tbody tr')
async function routeApi(route) {
  const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
  requests.push({ method: request.method(), endpoint, query: url.search,
    ...(['POST', 'PATCH'].includes(request.method()) ? { payload: request.postDataJSON() } : {}) })
  let status = 200; let body
  if (endpoint === '/api/auth/me') body = { username: 'fixture-operator', role }
  else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-schedule-csrf' }
  else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-backup-schedules' }
  else if (endpoint === '/api/system/modules') body = [{ key: 'devices', enabled: true }, { key: 'config-backups', enabled: true }]
  else if (endpoint === '/api/system/devices') body = { items: devices, total: devices.length, page: 1, pageSize: 100, hasMore: false, summary: { totalRecords: devices.length } }
  else if (endpoint === '/api/system/h3c/profiles') body = profiles
  else if (endpoint.startsWith('/api/system/devices/')) {
    const device = devices.find(item => item.id === decodeURIComponent(endpoint.split('/').at(-1)))
    if (!device) { status = 404; body = {} }
    else body = { item: device, terminalProfileId: device.terminalProfileId,
      terminalProfile: profiles.find(profile => profile.id === device.terminalProfileId), recentSessions: [], auditEvents: [] }
  } else if (endpoint === '/api/system/config-backups' && request.method() === 'GET') {
    const profileId = url.searchParams.get('profileId'); const configurationKind = url.searchParams.get('configurationKind') || 'RUNNING'
    body = [{ id: `${profileId}-${configurationKind}-v1`, profileId, configurationKind, version: 1, capturedMs: 1_800_000_000_000,
      byteCount: 1024, sha256: '0123456789abcdef'.repeat(4) }]
  } else if (endpoint === '/api/system/config-backup-schedules' && request.method() === 'GET') {
    const profileId = url.searchParams.get('profileId')
    body = [...schedules.values()].filter(item => !profileId || item.profileIds.includes(profileId))
  } else if (endpoint === '/api/system/config-backup-schedules' && request.method() === 'POST') {
    if (mode === 'create-error') { status = 503; body = { error: 'SCHEDULE_STORE_FAILED', detail: 'private-storage-detail' } }
    else {
      const payload = request.postDataJSON()
      const item = { id: `fixture-schedule-${++serial}`, ownerUserId: 'fixture-owner', ...payload, enabled: true,
        createdMs: 1_800_000_000_000, nextRunMs: 1_800_000_000_000 + payload.intervalMinutes * 60000, lastState: 'NEVER_RUN' }
      schedules.set(item.id, item); status = 201; body = item
    }
  } else if (endpoint.startsWith('/api/system/config-backup-schedules/')) {
    const id = decodeURIComponent(endpoint.split('/').at(-1)); const item = schedules.get(id)
    if (mode === 'scope-denied') { status = 403; body = { error: 'FORBIDDEN', detail: 'private-scope-detail' } }
    else if (!item) { status = 404; body = {} }
    else if (request.method() === 'PATCH') { body = { ...item, enabled: request.postDataJSON().enabled }; schedules.set(id, body) }
    else if (request.method() === 'DELETE') { schedules.delete(id); body = { deleted: true } }
    else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
  } else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) })
}
async function screenshot(name) {
  await page.screenshot({ path: path.join(output, name), fullPage: false, animations: 'disabled' })
  screenshots.push({ file: name, viewport: page.viewportSize() })
}
async function checkLayout() {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true)
  const panel = schedulePanel()
  expect(await panel.locator('.table-wrap').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  const box = await panel.boundingBox(); expect(box.x).toBeGreaterThanOrEqual(0)
  expect(box.x + box.width).toBeLessThanOrEqual(page.viewportSize().width)
}
async function createSchedule(kind, interval, expectedProfiles) {
  await page.getByRole('combobox', { name: '配置类别', exact: true }).selectOption(kind)
  await schedulePanel().getByRole('combobox', { name: '备份周期', exact: true }).selectOption(String(interval))
  const before = creates().length
  await schedulePanel().getByRole('button', { name: '为所选设备创建计划', exact: true }).click()
  await expect.poll(() => creates().length).toBe(before + 1)
  expect(creates().at(-1).payload).toEqual({ profileIds: expectedProfiles, configurationKind: kind, intervalMinutes: interval })
  await expect(schedulePanel().getByRole('button', { name: '为所选设备创建计划', exact: true })).toBeEnabled()
}
try {
  server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: 0 } })
  await server.listen(); report.resources.vitePort = server.httpServer.address().port
  const base = `http://127.0.0.1:${report.resources.vitePort}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1 })
  page.on('pageerror', error => errors.push(error.message)); await page.route(url => url.pathname.startsWith('/api/'), routeApi)
  await page.goto(`${base}/config-backups?deviceId=device-b`)
  await expect(page.getByRole('heading', { name: '交换机乙 · 合成样本 · 192.0.2.11 · 运行配置版本历史', exact: true })).toBeVisible()
  await expect(schedulePanel()).toContainText('暂无定时备份计划')
  expect(mutations()).toEqual([])
  expect(requests.filter(item => item.endpoint === '/api/system/config-backup-schedules').map(item => item.query)).toEqual(['?profileId=profile-b'])
  scenarios.push('opening-device-context-only-reads-the-exact-profile-and-does-not-run-backup')

  await createSchedule('SAVED', 360, ['profile-b'])
  await expect(scheduleRows()).toHaveCount(1); await expect(scheduleRows().first()).toContainText('1 台 · 保存配置')
  await expect(scheduleRows().first()).toContainText('每 6 小时'); await expect(scheduleRows().first()).toContainText('尚未执行')
  scenarios.push('create-saved-schedule-uses-exact-context-profile-category-and-six-hour-cycle')
  const savedId = [...schedules.keys()][0]
  await scheduleRows().first().getByRole('button', { name: '暂停', exact: true }).click()
  await expect(scheduleRows().first()).toContainText('已暂停')
  expect(mutations().at(-1)).toMatchObject({ method: 'PATCH', endpoint: `/api/system/config-backup-schedules/${savedId}`, payload: { enabled: false } })
  await expect(scheduleRows().first().locator('td').nth(3)).toHaveText('—')
  await scheduleRows().first().getByRole('button', { name: '启用', exact: true }).click()
  await expect(scheduleRows().first()).toContainText('已启用')
  expect(mutations().at(-1)).toMatchObject({ method: 'PATCH', endpoint: `/api/system/config-backup-schedules/${savedId}`, payload: { enabled: true } })
  scenarios.push('pause-and-enable-mutate-only-the-exact-schedule-and-refresh-the-list')

  const beforeReload = mutations().length
  await page.reload(); await expect(schedulePanel()).toContainText('1 台 · 保存配置')
  await expect(scheduleRows().first()).toContainText('已启用'); expect(mutations().length).toBe(beforeReload)
  scenarios.push('browser-reload-restores-schedules-from-mock-api-without-creating-or-running-anything')
  await createSchedule('STARTUP_INFO', 10080, ['profile-b'])
  await expect(scheduleRows()).toHaveCount(2)
  await expect(scheduleRows().filter({ hasText: '启动配置指向' })).toContainText('每 7 天')
  await expect(page.getByText('启动配置指向仅记录设备报告的启动文件信息，不包含文件正文，也不表示可以恢复配置。', { exact: true })).toBeVisible()
  scenarios.push('startup-info-plan-preserves-category-and-weekly-cycle-with-the-file-information-explanation')
  for (const viewport of viewports) {
    await page.setViewportSize(viewport); await schedulePanel().scrollIntoViewIfNeeded(); await checkLayout()
    await screenshot(`scheduled-backups-${viewport.width}.png`)
  }
  scenarios.push('schedule-controls-history-and-operation-columns-fit-1920x1080-and-2560x1440')

  await scheduleRows().filter({ hasText: '保存配置' }).getByRole('button', { name: '删除', exact: true }).click()
  await expect(scheduleRows()).toHaveCount(1); await expect(scheduleRows()).toContainText('启动配置指向')
  expect(mutations().at(-1)).toMatchObject({ method: 'DELETE', endpoint: `/api/system/config-backup-schedules/${savedId}` })
  expect(schedules.has(savedId)).toBe(false)
  scenarios.push('delete-removes-only-the-selected-plan-and-preserves-other-categories')

  await page.goto(`${base}/config-backups`)
  await expect(schedulePanel().getByRole('button', { name: '为所选设备创建计划', exact: true })).toBeDisabled()
  await page.getByRole('checkbox', { name: '192.0.2.11 · fixture-reader', exact: true }).check()
  await page.getByRole('checkbox', { name: '192.0.2.12 · fixture-reader', exact: true }).check()
  await createSchedule('RUNNING', 60, ['profile-b', 'profile-c'])
  await expect(scheduleRows().filter({ hasText: '运行配置' })).toContainText('2 台 · 运行配置')
  await expect(scheduleRows().filter({ hasText: '运行配置' })).toContainText('每 1 小时')
  expect(requests.filter(item => item.endpoint === '/api/system/config-backup-schedules' && item.method === 'GET').at(-1).query).toBe('')
  scenarios.push('general-page-requires-selection-and-creates-only-the-selected-profiles-with-running-category')

  mode = 'create-error'; const storedBeforeError = schedules.size
  await createSchedule('RUNNING', 1440, ['profile-b', 'profile-c'])
  await expect(schedulePanel().getByRole('status')).toContainText('定时备份操作失败，请稍后刷新重试。')
  await expect(schedulePanel()).not.toContainText('private-storage-detail'); expect(schedules.size).toBe(storedBeforeError)
  const beforeRefresh = mutations().length; mode = 'normal'
  await schedulePanel().getByRole('button', { name: '刷新计划', exact: true }).click()
  await expect(schedulePanel().getByRole('status')).toHaveCount(0); await expect(scheduleRows()).toHaveCount(2)
  expect(mutations().length).toBe(beforeRefresh)
  scenarios.push('failed-create-shows-simple-error-and-refresh-only-reads-the-existing-plans')

  mode = 'scope-denied'
  await scheduleRows().filter({ hasText: '运行配置' }).getByRole('button', { name: '暂停', exact: true }).click()
  await expect(schedulePanel().getByRole('status')).toContainText('计划不存在或当前权限不可用，请刷新设备资料。')
  await expect(schedulePanel()).not.toContainText('private-scope-detail'); await expect(schedulePanel()).toContainText('暂无定时备份计划')
  await expect(schedulePanel().getByRole('button', { name: '删除', exact: true })).toHaveCount(0)
  await screenshot('scheduled-backups-permission-revoked.png')
  scenarios.push('permission-revocation-clears-plan-actions-and-omits-private-error-details')

  mode = 'normal'; role = 'ADMIN'; const beforeRoleLoad = mutations().length
  await page.reload(); await expect(page.getByRole('heading', { name: '配置备份', exact: true })).toBeVisible()
  await expect(schedulePanel()).toHaveCount(0)
  await expect(page.getByRole('button', { name: '开始配置备份', exact: true })).toBeDisabled()
  expect(mutations().length).toBe(beforeRoleLoad)
  scenarios.push('admin-can-read-history-and-has-no-schedule-mutation-controls')
  expect(mutations().every(item => item.endpoint.startsWith('/api/system/config-backup-schedules'))).toBe(true)
  expect(JSON.stringify(mutations())).not.toMatch(/password|credential|command|executablePath/)
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  report.status = 'PASS'; fs.rmSync(path.join(output, 'failure.png'), { force: true })
} catch (error) {
  failure = error; report.status = 'FAIL'; report.failure = error instanceof Error ? error.message : String(error)
  if (page && !page.isClosed()) {
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: false }).catch(() => {})
    console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: (await page.locator('body').innerText()).slice(0, 14000) }))
  }
} finally {
  try { await browser?.close(); report.resources.ownedChromeClosed = Boolean(browser) }
  catch (error) { failure ||= error; report.status = 'FAIL'; report.chromeCleanupError = String(error) }
  try { await server?.close(); report.resources.ownedViteClosed = Boolean(server) }
  catch (error) { failure ||= error; report.status = 'FAIL'; report.viteCleanupError = String(error) }
  report.completedAt = new Date().toISOString()
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify(report))
}
if (failure) throw failure
