// Source Vue UI + owned Chrome/Vite with synthetic API responses; no backend or device writes.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'h3c-vlan-browser')
fs.mkdirSync(output, { recursive: true })
const errors = []; const unexpected = []; const scenarios = []; const screenshots = []
const factReads = []; const definitionPosts = []; const runPosts = []
const viewports = [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]
const result = {
  status: 'RUNNING', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING',
  backendExercised: false, deviceConnections: 0, databaseWrites: 0,
  startedAt: new Date().toISOString(), viewports, deviceScaleFactor: 1, screenshotMode: 'VIEWPORT',
  scenarios, screenshots, factReads, definitionPosts, runPosts, errors, unexpected,
  resources: { vitePort: null, ownedViteClosed: false, ownedChromeClosed: false },
}
let server; let browser; let page; let failure
let mode = 'normal'
const profile = { id: 'mock-h3c-profile', host: '192.0.2.10', port: 22, username: 'fixture-reader', enabled: true,
  terminalProtocol: 'SSH', supportedProtocols: ['SSH'], hostKeyUpdatePolicy: 'AUTO_UPDATE', terminalEncoding: 'AUTO' }
const capturedMs = 1_800_000_000_000
const vlan = { vlanId: 10, name: 'PIS-VLAN（模拟输出）', description: '列车信息测试网（模拟输出）', type: 'Static',
  routeInterface: true, taggedPorts: ['GigabitEthernet1/0/1'], untaggedPorts: ['GigabitEthernet1/0/2'] }
function snapshot(id, overrides = {}, payload = {}) {
  return { id, profileId: profile.id, kind: 'vlan.table', identityKey: 'default', status: 'STALE',
    source: `H3C_TASK_RAW:${id}`, capturedMs, isCurrent: true, collectedRawVerified: true,
    dataState: 'COLLECTED_RAW_VERIFIED', freshness: 'AGE_DEPENDENT',
    payloadJson: JSON.stringify({ command: 'display vlan all', status: 'PARSED', vlans: [vlan], notice: null,
      capturedScope: { version: 1, deviceId: 'mock-device', profileId: profile.id, siteId: 'mock-site' },
      rawProvenance: { version: 1, runId: id, command: 'display vlan all', capturedMs }, ...payload }), ...overrides }
}
const current = snapshot('mock-current')
const historical = snapshot('mock-historical', { isCurrent: false, capturedMs: capturedMs - 60_000 }, {
  vlans: [{ ...vlan, vlanId: 20, name: '历史-VLAN（模拟输出）', routeInterface: null, taggedPorts: [], untaggedPorts: [] }],
})
const interfaces = Array.from({ length: 100 }, (_, index) => ({
  id: `mock-interface-${index}`, profileId: profile.id, kind: 'interface', identityKey: `GigabitEthernet1/0/${index + 1}`,
  status: 'STALE', source: 'SYNTHETIC_INTERFACE_FIXTURE', capturedMs, isCurrent: true,
  payloadJson: JSON.stringify({ interfaceName: `GigabitEthernet1/0/${index + 1}`, linkStatus: 'UP' }),
}))

try {
  server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'),
    server: { host: '127.0.0.1', port: 0 } })
  await server.listen()
  result.resources.vitePort = server.httpServer.address().port
  const base = `http://127.0.0.1:${result.resources.vitePort}`
  browser = await chromium.launch({ headless: true,
    executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1 })
  page.on('pageerror', error => errors.push(error.message))
  await page.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
    if (!endpoint.startsWith('/api/')) return route.continue()
    let status = 200; let body
    if (endpoint === '/api/auth/me') body = { username: 'fixture-reader', role: 'SUPERADMIN' }
    else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-vlan-csrf' }
    else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-vlan-ui' }
    else if (endpoint === '/api/system/modules') body = ['h3c', 'tasks', 'devices'].map(key => ({ key, enabled: true }))
    else if (endpoint === '/api/system/devices') body = { items: [], total: 0, page: 1, pageSize: 1, hasMore: false, summary: { totalRecords: 0 } }
    else if (endpoint === '/api/system/h3c/profiles') body = [profile]
    else if (endpoint === `/api/system/h3c/profiles/${profile.id}/facts`) {
      const kind = url.searchParams.get('kind'); const history = url.searchParams.get('history') === 'true'
      if (kind === 'vlan.table') {
        if (mode === 'denied') { status = 403; body = { error: 'FORBIDDEN', detail: 'Synthetic VLAN read denied' } }
        else if (mode === 'partial') body = [snapshot('mock-partial', { status: 'PARTIAL', dataState: 'PARTIAL' },
          { status: 'PARTIAL', notice: '部分输出：模拟格式变化，未完整解析。' })]
        else body = history ? [current, historical] : [current]
      } else body = interfaces
      factReads.push({ kind, history, requestedLimit: Number(url.searchParams.get('limit')), status,
        returnedCount: Array.isArray(body) ? body.length : 0 })
    } else if (endpoint === `/api/system/h3c/profiles/${profile.id}/lldp-relations`) body = []
    else if (endpoint === `/api/system/h3c/profiles/${profile.id}/lldp-claims`) body = { profileId: profile.id,
      scopeState: 'GLOBAL', isGlobal: true, allowedSiteReferences: [], claimsRevision: 'synthetic-claims', factCount: 0 }
    else if (endpoint === '/api/system/tasks' && request.method() === 'GET') body = []
    else if (endpoint === '/api/system/h3c/task-definitions' && request.method() === 'POST') {
      body = request.postDataJSON(); definitionPosts.push(body); status = 201
    } else if (/^\/api\/system\/tasks\/[^/]+\/runs$/.test(endpoint) && request.method() === 'POST') {
      runPosts.push({ definitionId: decodeURIComponent(endpoint.split('/')[4]), body: request.postDataJSON() })
      body = { runId: 'mock-vlan-run' }; status = 202
    } else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  })

  async function screenshot(name) {
    await page.screenshot({ path: path.join(output, name), fullPage: false })
    screenshots.push({ file: name, viewport: page.viewportSize() })
  }
  const content = page.locator('main .page-scroll')
  const vlanPanel = page.locator('.vlan-facts')
  await page.goto(`${base}/h3c`)
  await expect(page.getByRole('heading', { name: 'H3C 事实回放', exact: true })).toBeVisible()
  await expect(vlanPanel.locator('.vlan-snapshot')).toHaveCount(1)
  await expect(vlanPanel).toContainText('PIS-VLAN（模拟输出）')
  expect(factReads.some(read => read.kind === null && read.returnedCount === 100 && read.requestedLimit === 100)).toBe(true)
  expect(factReads.some(read => read.kind === 'vlan.table' && read.returnedCount === 1 && !read.history)).toBe(true)
  await expect(content.locator('.inventory-summary')).toContainText('101 条事实')
  const cells = vlanPanel.locator('tbody tr').first().locator('td')
  expect(await cells.allTextContents()).toEqual(['10', 'PIS-VLAN（模拟输出）', '列车信息测试网（模拟输出）', 'Static', '有', 'GigabitEthernet1/0/1', 'GigabitEthernet1/0/2'])
  await expect(vlanPanel).toContainText('H3C_TASK_RAW:mock-current')
  await expect(vlanPanel).toContainText('捕获时间：')
  await expect(vlanPanel).toContainText('Raw 完整性已校验，解析结果以上述状态为准')
  expect(definitionPosts).toEqual([]); expect(runPosts).toEqual([])
  scenarios.push('general-facts-limit-100-does-not-hide-dedicated-collected-vlan-snapshot')

  for (const viewport of viewports) {
    await page.setViewportSize(viewport)
    await vlanPanel.evaluate(element => element.scrollIntoView({ block: 'start' }))
    await expect(vlanPanel.getByRole('heading', { name: 'VLAN 采集快照', exact: true })).toBeVisible()
    await expect(vlanPanel.getByRole('cell', { name: 'GigabitEthernet1/0/2', exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    expect(await vlanPanel.locator('.table-wrap').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
    const box = await vlanPanel.boundingBox()
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(viewport.width)
    await screenshot(`vlan-current-${viewport.width}.png`)
  }
  scenarios.push('rendered-seven-column-vlan-table-fits-1920x1080-and-2560x1440')

  await content.getByRole('button', { name: '查看历史', exact: true }).click()
  await expect(vlanPanel.locator('.vlan-snapshot')).toHaveCount(2)
  const snapshots = vlanPanel.locator('.vlan-snapshot')
  await expect(snapshots.nth(0)).toContainText('当前快照')
  await expect(snapshots.nth(0)).not.toContainText('历史-VLAN（模拟输出）')
  await expect(snapshots.nth(1)).toContainText('历史快照')
  await expect(snapshots.nth(1)).toContainText('历史-VLAN（模拟输出）')
  await expect(snapshots.nth(1)).not.toContainText('PIS-VLAN（模拟输出）')
  expect(factReads.some(read => read.kind === 'vlan.table' && read.history && read.requestedLimit === 500)).toBe(true)
  await vlanPanel.evaluate(element => element.scrollIntoView({ block: 'start' }))
  await screenshot('vlan-current-and-history.png')
  await content.getByRole('button', { name: '仅当前', exact: true }).click()
  await expect(vlanPanel.locator('.vlan-snapshot')).toHaveCount(1)
  await expect(vlanPanel).not.toContainText('历史-VLAN（模拟输出）')
  scenarios.push('current-and-historical-whole-snapshots-remain-separate-and-history-toggle-clears-old-rows')

  mode = 'partial'
  await content.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(vlanPanel.locator('.vlan-snapshot .state-pill')).toHaveText('部分解析')
  await expect(vlanPanel.locator('.state-pill.good')).toHaveCount(0)
  await expect(vlanPanel).not.toContainText('解析完成')
  await expect(vlanPanel).toContainText('部分输出：模拟格式变化，未完整解析。')
  await expect(vlanPanel).toContainText('Raw 完整性已校验')
  await vlanPanel.evaluate(element => element.scrollIntoView({ block: 'start' }))
  await screenshot('vlan-partial.png')
  scenarios.push('partial-parser-result-never-appears-complete-even-with-verified-raw')

  mode = 'denied'
  await content.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(content.getByRole('alert')).toContainText('事实读取失败，请刷新重试。')
  await expect(vlanPanel.locator('.vlan-snapshot')).toHaveCount(0)
  await expect(vlanPanel).toContainText('VLAN 事实暂不可用')
  await expect(vlanPanel).not.toContainText('PIS-VLAN（模拟输出）')
  expect(factReads.at(-1)).toMatchObject({ kind: 'vlan.table', status: 403 })
  await vlanPanel.evaluate(element => element.scrollIntoView({ block: 'start' }))
  await screenshot('vlan-read-denied.png')
  scenarios.push('dedicated-vlan-read-denial-clears-old-snapshot-without-general-facts-fallback')

  mode = 'normal'
  await page.goto(`${base}/tasks`)
  await expect(page.getByRole('heading', { name: '任务中心', exact: true })).toBeVisible()
  const vlanOption = page.getByRole('checkbox', { name: 'VLAN 信息', exact: true })
  await expect(vlanOption).not.toBeChecked()
  await expect(page.getByText('连接时自动使用设备已保存的用户名和密码，无需重复输入。', { exact: true })).toBeVisible()
  await expect(page.getByText('Credential Reference', { exact: false })).toHaveCount(0)
  expect(definitionPosts).toEqual([]); expect(runPosts).toEqual([])
  await page.getByRole('checkbox', { name: '设备版本', exact: true }).uncheck()
  await vlanOption.check()
  await expect(vlanOption).toBeChecked()
  expect(definitionPosts).toEqual([]); expect(runPosts).toEqual([])
  await screenshot('tasks-vlan-explicit-selection.png')
  await page.getByRole('button', { name: '创建并执行计划', exact: true }).click()
  await expect.poll(() => runPosts.length).toBe(1)
  expect(definitionPosts).toHaveLength(1)
  expect(definitionPosts[0]).toMatchObject({ profileId: profile.id, command: 'display vlan all',
    commands: ['display vlan all'], timeoutMs: 30000, enabled: true })
  expect(Object.keys(definitionPosts[0]).sort()).toEqual(['command', 'commands', 'enabled', 'id', 'profileId', 'timeoutMs'])
  expect(runPosts).toEqual([{ definitionId: definitionPosts[0].id, body: {} }])
  await expect(page.getByText('已提交 1 条命令的采集计划', { exact: true })).toBeVisible()
  scenarios.push('vlan-task-option-defaults-off-and-only-explicit-submit-sends-fixed-readonly-command')
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  result.status = 'PASS'
  fs.rmSync(path.join(output, 'failure.png'), { force: true })
} catch (error) {
  failure = error; result.status = 'FAIL'; result.failure = error instanceof Error ? error.message : String(error)
  if (page && !page.isClosed()) {
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
    console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: (await page.locator('body').innerText()).slice(0, 12_000) }))
  }
} finally {
  try { await browser?.close(); result.resources.ownedChromeClosed = Boolean(browser) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.chromeCleanupError = String(error) }
  try { await server?.close(); result.resources.ownedViteClosed = Boolean(server) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.viteCleanupError = String(error) }
  result.completedAt = new Date().toISOString()
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result))
}
if (failure) throw failure
