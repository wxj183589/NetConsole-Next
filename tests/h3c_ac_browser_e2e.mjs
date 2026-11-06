// Source Vue UI + owned Chrome/Vite with synthetic API responses; no backend/device writes.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'h3c-ac-browser')
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
const profile = { id: 'mock-ac-profile', host: '192.0.2.71', port: 22, username: 'fixture-reader', enabled: true,
  terminalProtocol: 'SSH', supportedProtocols: ['SSH'], hostKeyUpdatePolicy: 'AUTO_UPDATE', terminalEncoding: 'AUTO' }
const required = ['display wlan ap all', 'display wlan ap all address', 'display wlan ap all radio']
const optional = ['display wlan ap all radio verbose filter bbssid', 'display wlan ap all connection-record',
  'display wlan ap all radio type', 'display wlan ap unauthenticated', 'display wlan ap all lldp', 'display wlan ap all verbose']
const overview = ['display cpu-usage', 'display memory', 'display device manuinfo']
const commands = [...required, ...optional, ...overview]
const kind = command => `command.${command.replaceAll(' ', '.')}`
const capturedMs = 1_800_000_000_000
const escapedOutput = '<img src=x onerror="window.__acInjected=true"> 设备回显按文本显示'
function snapshot(command, id, overrides = {}, payload = {}) {
  return { id, profileId: profile.id, kind: kind(command), identityKey: 'default', status: 'STALE',
    source: `H3C_TASK_RAW:${id}`, capturedMs, isCurrent: true, collectedRawVerified: true,
    dataState: 'COLLECTED_RAW_VERIFIED', freshness: 'AGE_DEPENDENT',
    payloadJson: JSON.stringify({ command, status: 'COMPLETE', output: `Synthetic ${command}\n${escapedOutput}`,
      notice: null, capturedScope: { version: 1, deviceId: 'mock-ac-device', profileId: profile.id, siteId: 'mock-site' },
      rawProvenance: { version: 1, runId: id, command, capturedMs }, ...payload }), ...overrides }
}
const current = commands.map((command, index) => snapshot(command, `mock-ac-${index}`))
const historical = snapshot(required[2], 'mock-ac-history', { isCurrent: false, capturedMs: capturedMs - 60_000 },
  { output: 'HISTORICAL_AP_RADIO Channel 36' })
const failureStates = ['UNSUPPORTED', 'PARTIAL', 'EMPTY', 'FAILED'].map((status, index) =>
  snapshot(optional[index + 1], `mock-ac-${status}`, { status, dataState: 'COLLECTED_RAW_VERIFIED' },
    { status, output: status === 'EMPTY' ? '' : `Synthetic ${status}`, notice: `模拟命令状态：${status}` }))
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
    else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-ac-csrf' }
    else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-ac-ui' }
    else if (endpoint === '/api/system/modules') body = ['h3c', 'tasks', 'devices'].map(key => ({ key, enabled: true }))
    else if (endpoint === '/api/system/devices') body = { items: [], total: 0, page: 1, pageSize: 1, hasMore: false, summary: { totalRecords: 0 } }
    else if (endpoint === '/api/system/h3c/profiles') body = [profile]
    else if (endpoint === `/api/system/h3c/profiles/${profile.id}/facts`) {
      const requestedKind = url.searchParams.get('kind'); const history = url.searchParams.get('history') === 'true'
      if (requestedKind?.startsWith('command.')) {
        if (mode === 'denied' && requestedKind === kind(required[2])) {
          status = 403; body = { error: 'FORBIDDEN', detail: 'Synthetic AC read denied' }
        } else {
          const available = mode === 'failures' ? failureStates : current
          body = available.filter(item => item.kind === requestedKind)
          if (history && requestedKind === historical.kind && mode !== 'failures') body.push(historical)
        }
      } else body = requestedKind === 'vlan.table' ? [] : interfaces
      factReads.push({ kind: requestedKind, history, requestedLimit: Number(url.searchParams.get('limit')), status,
        returnedCount: Array.isArray(body) ? body.length : 0 })
    } else if (endpoint === `/api/system/h3c/profiles/${profile.id}/lldp-relations`) body = []
    else if (endpoint === `/api/system/h3c/profiles/${profile.id}/lldp-claims`) body = { profileId: profile.id,
      scopeState: 'GLOBAL', isGlobal: true, allowedSiteReferences: [], claimsRevision: 'synthetic-claims', factCount: 0 }
    else if (endpoint === '/api/system/tasks' && request.method() === 'GET') body = []
    else if (endpoint === '/api/system/h3c/task-definitions' && request.method() === 'POST') {
      body = request.postDataJSON(); definitionPosts.push(body); status = 201
    } else if (/^\/api\/system\/tasks\/[^/]+\/runs$/.test(endpoint) && request.method() === 'POST') {
      runPosts.push({ definitionId: decodeURIComponent(endpoint.split('/')[4]), body: request.postDataJSON() })
      body = { runId: 'mock-ac-run' }; status = 202
    } else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  })

  async function screenshot(name) {
    await page.screenshot({ path: path.join(output, name), fullPage: false })
    screenshots.push({ file: name, viewport: page.viewportSize() })
  }
  const content = page.locator('main .page-scroll')
  const panel = page.locator('.ac-facts')
  await page.goto(`${base}/h3c`)
  await expect(page.getByRole('heading', { name: 'H3C 事实回放', exact: true })).toBeVisible()
  await expect(panel.locator('.ac-snapshot')).toHaveCount(12)
  await expect(content.locator('.inventory-summary')).toContainText('112 条事实')
  expect(factReads.some(read => read.kind === null && read.returnedCount === 100 && read.requestedLimit === 100)).toBe(true)
  expect(factReads.filter(read => read.kind?.startsWith('command.') && !read.history && read.requestedLimit === 1)).toHaveLength(12)
  const radio = panel.locator('.ac-snapshot').filter({ has: page.getByRole('heading', { name: 'AP Radio · 当前回显', exact: true }) })
  await expect(radio).toContainText('捕获时间：')
  await expect(radio).toContainText('H3C_TASK_RAW:mock-ac-2')
  await expect(radio.locator('.state-pill')).toHaveText('采集回显已校验')
  await radio.locator('summary').click()
  await expect(radio.locator('pre')).toContainText(escapedOutput)
  await expect(panel.locator('img')).toHaveCount(0)
  expect(await page.evaluate(() => window.__acInjected)).toBeUndefined()
  expect(definitionPosts).toEqual([]); expect(runPosts).toEqual([])
  scenarios.push('all-12-command-replies-have-dedicated-reads-despite-100-general-facts-and-no-implicit-collection')
  scenarios.push('radio-source-time-verified-raw-status-and-device-output-render-as-escaped-text')

  for (const viewport of viewports) {
    await page.setViewportSize(viewport)
    await panel.evaluate(element => element.scrollIntoView({ block: 'start' }))
    await expect(panel.getByRole('heading', { name: 'AC / FIT-AP 采集回显', exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    const box = await panel.boundingBox()
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(viewport.width)
    await screenshot(`ac-current-${viewport.width}.png`)
  }
  scenarios.push('ac-command-evidence-fits-1920x1080-and-2560x1440')

  await content.getByRole('button', { name: '查看历史', exact: true }).click()
  await expect(panel.locator('.ac-snapshot')).toHaveCount(13)
  const history = panel.locator('.ac-snapshot').filter({ has: page.getByRole('heading', { name: 'AP Radio · 历史回显', exact: true }) })
  await expect(history).toContainText('H3C_TASK_RAW:mock-ac-history')
  await history.locator('summary').click()
  await expect(history.locator('pre')).toHaveText('HISTORICAL_AP_RADIO Channel 36')
  await expect(radio).not.toContainText('HISTORICAL_AP_RADIO')
  expect(factReads.some(read => read.kind === kind(required[2]) && read.history && read.requestedLimit === 20)).toBe(true)
  await history.evaluate(element => element.scrollIntoView({ block: 'center' }))
  await screenshot('ac-current-and-history.png')
  await content.getByRole('button', { name: '仅当前', exact: true }).click()
  await expect(panel.locator('.ac-snapshot')).toHaveCount(12)
  await expect(panel).not.toContainText('HISTORICAL_AP_RADIO')
  scenarios.push('history-keeps-command-captures-separate-with-limit-20-and-clears-old-history-on-toggle')

  mode = 'failures'
  await content.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(panel.locator('.ac-snapshot')).toHaveCount(4)
  for (const label of ['命令不支持/未识别', '部分输出', '无数据', '采集失败']) {
    await expect(panel.locator('.ac-snapshot .state-pill').filter({ hasText: label })).toHaveCount(1)
  }
  await expect(panel).not.toContainText('采集回显已校验')
  await panel.evaluate(element => element.scrollIntoView({ block: 'start' }))
  await screenshot('ac-optional-failure-states.png')
  scenarios.push('unsupported-partial-empty-failed-states-override-inconsistent-raw-verification-label')

  mode = 'denied'
  await content.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(content.getByRole('alert')).toContainText('事实读取失败，请刷新重试。')
  await expect(panel.locator('.ac-snapshot')).toHaveCount(0)
  await expect(panel).toContainText('AC 采集回显暂不可用')
  await panel.evaluate(element => element.scrollIntoView({ block: 'start' }))
  await screenshot('ac-read-denied.png')
  scenarios.push('one-command-read-denial-clears-old-ac-evidence-without-general-facts-fallback')

  mode = 'normal'
  await page.goto(`${base}/tasks`)
  await expect(page.getByRole('heading', { name: '任务中心', exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: '设备版本', exact: true })).toBeChecked()
  for (const label of ['FIT-AP 列表', 'FIT-AP 地址', 'FIT-AP 射频']) {
    await expect(page.getByRole('checkbox', { name: label, exact: true })).not.toBeChecked()
  }
  await page.getByRole('button', { name: '选择 AC 基础采集', exact: true }).click()
  await expect(page.getByRole('checkbox', { name: '设备版本', exact: true })).not.toBeChecked()
  for (const label of ['FIT-AP 列表', 'FIT-AP 地址', 'FIT-AP 射频']) {
    await expect(page.getByRole('checkbox', { name: label, exact: true })).toBeChecked()
  }
  expect(definitionPosts).toEqual([]); expect(runPosts).toEqual([])
  expect(await page.getByRole('checkbox').evaluateAll(elements => elements.filter(element => element.checked).length)).toBe(3)
  await expect(page.getByText('连接时自动使用设备已保存的用户名和密码，无需重复输入。', { exact: true })).toBeVisible()
  await expect(page.getByText('Credential Reference', { exact: false })).toHaveCount(0)
  await screenshot('tasks-ac-explicit-selection.png')
  await page.getByRole('button', { name: '创建并执行计划', exact: true }).click()
  await expect.poll(() => runPosts.length).toBe(1)
  expect(definitionPosts).toHaveLength(1)
  expect(definitionPosts[0]).toMatchObject({ profileId: profile.id, command: required[0],
    commands: required, timeoutMs: 60000, enabled: true })
  expect(Object.keys(definitionPosts[0]).sort()).toEqual(['command', 'commands', 'enabled', 'id', 'profileId', 'timeoutMs'])
  expect(runPosts).toEqual([{ definitionId: definitionPosts[0].id, body: {} }])
  await expect(page.getByText('已提交 3 条命令的采集计划', { exact: true })).toBeVisible()
  scenarios.push('ac-preset-only-selects-required-three-and-explicit-submit-uses-saved-credentials-and-60-second-plan')
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
