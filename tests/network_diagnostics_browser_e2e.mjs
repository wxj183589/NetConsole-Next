// Owned source Vite + Chrome and synthetic API responses; no service/device calls.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'network-diagnostics-browser')
fs.mkdirSync(output, { recursive: true })
const scenarios = []; const screenshots = []; const requests = []; const errors = []; const unexpected = []
const viewports = [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]
const result = { status: 'RUNNING', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING',
  backendExercised: false, deviceConnections: 0, databaseWrites: 0, startedAt: new Date().toISOString(), viewports,
  deviceScaleFactors: [1, 1.25], scalingEvidence: 'BROWSER_DEVICE_SCALE_FACTOR_ONLY', screenshotMode: 'VIEWPORT',
  scenarios, screenshots, requests, errors, unexpected, resources: { vitePort: null, ownedViteClosed: false, ownedChromeClosed: false } }
let server; let browser; let page; let failure
let mode = 'success'; let role = 'SUPERADMIN'; let currentId = ''; let pollReads = 0; let cancelled = false; let serial = 0
const history = new Map()
const devices = [
  { id: 'mock-device-one', name: '核心交换机 · 合成样本', managementIp: '192.0.2.10', enabled: true, isValid: true },
  { id: 'mock-device-two', name: '边界路由器 · 合成样本', managementIp: '192.0.2.20', enabled: true, isValid: true },
]
function traceItem(device, state = 'SUCCEEDED', partial = false) {
  const first = device === devices[0]
  return { id: `${device.id}-trace`, deviceId: device.id, deviceName: device.name, siteId: 'mock-site', kind: 'TRACEROUTE', state,
    outcome: state === 'CANCELLED' ? 'CANCELLED' : partial ? 'TIMEOUT' : 'OK', detail: state === 'RUNNING' ? '正在探测路径' : null,
    latencyMs: state === 'RUNNING' ? null : partial ? 30000 : 16, evidenceCount: 0, evidenceSources: [],
    traceRoute: { destinationAddress: device.managementIp, destinationReached: !partial && state === 'SUCCEEDED', maxHops: 30,
      hops: state === 'RUNNING' ? [{ ttl: 1, address: '192.0.2.1', status: 'TTL_EXPIRED', latencyMs: 2 }]
        : first ? [{ ttl: 1, address: '192.0.2.1', status: 'TTL_EXPIRED', latencyMs: 2 },
          { ttl: 2, address: null, status: 'TIMED_OUT', latencyMs: null },
          ...(!partial ? [{ ttl: 3, address: device.managementIp, status: 'REACHED', latencyMs: 3 }] : [])]
          : [{ ttl: 1, address: '192.0.2.1', status: 'TTL_EXPIRED', latencyMs: 2 },
            { ttl: 2, address: null, status: 'TIMED_OUT', latencyMs: null }] } }
}
function run(state = 'RUNNING') {
  const terminal = !['QUEUED', 'RUNNING'].includes(state)
  return { id: currentId, actor: 'fixture-operator', state, timeoutMs: 30000, totalDevices: 2, totalChecks: 2,
    completedChecks: terminal ? 2 : 0, succeededChecks: state === 'PARTIAL' ? 1 : 0, failedChecks: state === 'PARTIAL' ? 1 : 0,
    noEvidenceChecks: 0, cancelledChecks: state === 'CANCELLED' ? 2 : 0, queuedChecks: 0, runningChecks: terminal ? 0 : 2,
    progressPercent: terminal ? 100 : 0, createdMs: 1_800_000_000_000 + serial * 1000,
    failureReason: state === 'PARTIAL' ? '部分诊断未成功；请查看设备明细。' : null,
    items: state === 'PARTIAL' ? [traceItem(devices[0]), traceItem(devices[1], 'FAILED', true)]
      : devices.map(device => traceItem(device, state === 'CANCELLED' ? 'CANCELLED' : 'RUNNING', state === 'CANCELLED')) }
}
function summary(value) {
  return { id: value.id, state: value.state, totalDevices: value.totalDevices, totalChecks: value.totalChecks,
    completedChecks: value.completedChecks, progressPercent: value.progressPercent, createdMs: value.createdMs,
    kinds: [...new Set(value.items.map(item => item.kind))] }
}
async function routeApi(route) {
  const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
  requests.push({ method: request.method(), endpoint, ...(request.method() === 'POST' ? { payload: request.postDataJSON() } : {}) })
  let status = 200; let body
  if (endpoint === '/api/auth/me') body = { username: 'fixture-operator', role }
  else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-diagnostics-csrf' }
  else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-network-tools-ui' }
  else if (endpoint === '/api/system/modules') body = ['tools', 'devices'].map(key => ({ key, enabled: true }))
  else if (endpoint === '/api/system/devices') body = { items: devices, total: 2, page: 1, pageSize: 100, hasMore: false, summary: { totalRecords: 2 } }
  else if (endpoint === '/api/system/host-tools') body = [{ id: 'putty', displayName: 'PuTTY', product: 'PuTTY', status: 'READY', executablePath: 'D:\\synthetic-tools\\putty.exe' }]
  else if (endpoint === '/api/system/terminal/devices') body = [{ id: 'mock-terminal', host: '192.0.2.10', port: 22, username: 'reader' }]
  else if (endpoint === '/api/system/diagnostics/runs' && request.method() === 'GET') body = [...history.values()]
    .sort((left, right) => right.createdMs - left.createdMs).slice(0, Number(url.searchParams.get('limit') || 50)).map(summary)
  else if (endpoint === '/api/system/diagnostics/runs' && request.method() === 'POST') {
    if (mode === 'start-error') { status = 503; body = { error: 'DIAGNOSTIC_FAILED', detail: '诊断任务启动失败（合成场景）' } }
    else { currentId = `mock-diagnostic-${++serial}`; pollReads = 0; cancelled = false; status = 202; body = run(); history.set(currentId, body) }
  } else if (endpoint === `/api/system/diagnostics/runs/${currentId}/cancel` && request.method() === 'POST') {
    cancelled = true; body = { cancelled: true }
  } else if (/^\/api\/system\/diagnostics\/runs\/[^/]+$/.test(endpoint) && request.method() === 'GET') {
    const id = endpoint.split('/').at(-1); const retained = history.get(id)
    if (!retained) { status = 404; body = {} }
    else if (id !== currentId || !['QUEUED', 'RUNNING'].includes(retained.state)) body = retained
    else {
      pollReads++
      if (mode === 'poll-error' && pollReads === 1) { status = 503; body = { error: 'DIAGNOSTIC_READ_FAILED', detail: '诊断进度读取失败（合成场景）' } }
      else { body = cancelled ? run('CANCELLED') : mode === 'cancel' || pollReads === 1 ? run() : run('PARTIAL'); history.set(id, body) }
    }
  } else { status = 404; body = {}; unexpected.push(`${request.method()} ${endpoint}`) }
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) })
}
async function selectTrace() {
  await page.getByLabel('诊断设备', { exact: true }).selectOption(devices.map(device => device.id))
  for (const name of ['Ping', 'TCP 端口', 'DNS']) await page.getByRole('checkbox', { name, exact: true }).uncheck()
  await page.getByRole('checkbox', { name: 'Traceroute 路径', exact: true }).check()
  await expect(page.getByLabel('每项检查总超时', { exact: true })).toHaveValue('30000')
}
async function screenshot(name, scale = 1) {
  await page.screenshot({ path: path.join(output, name), fullPage: false, animations: 'disabled' })
  screenshots.push({ file: name, viewport: page.viewportSize(), deviceScaleFactor: scale })
}
async function checkLayout() {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
  expect(await page.locator('.diagnostic-results').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  for (const route of await page.locator('.trace-route').all()) {
    const bounds = await route.boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(0)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(page.viewportSize().width)
  }
}
try {
  server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: 0 } })
  await server.listen(); result.resources.vitePort = server.httpServer.address().port
  const base = `http://127.0.0.1:${result.resources.vitePort}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1 })
  page.on('pageerror', error => errors.push(error.message)); await page.route(url => url.pathname.startsWith('/api/'), routeApi)
  await page.goto(`${base}/tools`)
  await expect(page.getByRole('heading', { name: '网络诊断', exact: true })).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Traceroute 路径', exact: true })).not.toBeChecked()
  expect(requests.filter(request => request.method !== 'GET')).toEqual([])
  scenarios.push('opening-tools-only-reads-and-traceroute-remains-optional')
  await selectTrace(); await page.getByRole('button', { name: '开始诊断', exact: true }).click()
  await expect(page.getByRole('button', { name: '开始诊断', exact: true })).toBeDisabled()
  await expect(page.locator('.trace-route').first()).toContainText('正在探测路径')
  await expect(page.locator('.trace-route').first()).toContainText('已记录 1 / 30 跳')
  await screenshot('traceroute-running-1920.png')
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  await expect(page.locator('.trace-route').first()).toContainText('已到达目标')
  await expect(page.locator('.trace-route').nth(1)).toContainText('未到达目标')
  await expect(page.locator('.trace-route').nth(1)).toContainText('该跳超时')
  expect(requests.find(request => request.method === 'POST').payload).toEqual({ deviceIds: devices.map(device => device.id), kinds: ['TRACEROUTE'], timeoutMs: 30000 })
  scenarios.push('batch-uuid-request-and-live-hop-progress-followed-by-reached-and-timed-out-results')
  for (const viewport of viewports) {
    await page.setViewportSize(viewport); await checkLayout(); await screenshot(`traceroute-results-${viewport.width}.png`)
  }
  scenarios.push('hop-tables-and-controls-fit-1920x1080-and-2560x1440-without-horizontal-overflow')
  const savedId = currentId
  const startsBeforeHistory = requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length
  await page.getByRole('button', { name: '刷新历史', exact: true }).click()
  await expect(page.getByLabel('诊断历史', { exact: true })).toHaveValue(savedId)
  await page.reload()
  await expect(page.getByLabel('诊断历史', { exact: true }).locator(`option[value="${savedId}"]`)).toHaveCount(1)
  await expect(page.locator('.diagnostic-results')).toHaveCount(0)
  await page.getByLabel('诊断历史', { exact: true }).selectOption(savedId)
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  await expect(page.locator('.trace-route').nth(1)).toContainText('该跳超时')
  expect(requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length).toBe(startsBeforeHistory)
  await checkLayout(); await screenshot('diagnostic-history-reopened.png')
  scenarios.push('history-refresh-and-page-reload-discover-and-reopen-results-without-new-diagnostics')
  await selectTrace()
  mode = 'cancel'; await page.getByRole('button', { name: '开始诊断', exact: true }).click()
  await expect(page.locator('.trace-route').first()).toContainText('正在探测路径')
  await page.getByRole('button', { name: '取消任务', exact: true }).click()
  await expect(page.getByRole('heading', { name: '网络诊断', exact: true }).locator('..').locator('..')).toContainText('已取消 · 100%')
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  await expect(page.locator('.trace-route').first()).toContainText('未到达目标')
  await expect(page.locator('.trace-route').first()).toContainText('已记录 2 / 30 跳')
  await expect(page.getByRole('button', { name: '取消任务', exact: true })).toBeDisabled()
  await screenshot('traceroute-cancelled-partial.png')
  scenarios.push('cancel-retains-acquired-hops-and-does-not-claim-destination-reached')
  mode = 'poll-error'; const startsBeforeRetry = requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length
  await page.getByRole('button', { name: '开始诊断', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('诊断进度读取失败')
  await screenshot('traceroute-poll-error.png')
  await page.getByRole('button', { name: '重新读取进度', exact: true }).click()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  expect(requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length).toBe(startsBeforeRetry + 1)
  scenarios.push('progress-error-is-visible-and-retry-only-reads-the-existing-run')
  mode = 'start-error'; await page.getByRole('button', { name: '开始诊断', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('诊断任务启动失败')
  await expect(page.getByRole('button', { name: '开始诊断', exact: true })).toBeEnabled()
  scenarios.push('start-error-remains-visible-and-releases-the-submit-button')
  role = 'ADMIN'; await page.reload()
  await expect(page.getByRole('button', { name: '开始诊断', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: '取消任务', exact: true })).toBeDisabled()
  scenarios.push('lower-role-cannot-start-or-cancel-diagnostics')
  const startsBeforeReader = requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length
  await page.getByLabel('诊断历史', { exact: true }).selectOption(savedId)
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  await expect(page.locator('.trace-route').first()).toContainText('已到达目标')
  history.delete(savedId)
  await page.getByRole('button', { name: '刷新历史', exact: true }).click()
  await expect(page.locator('.diagnostic-results')).toHaveCount(0)
  await expect(page.getByLabel('诊断历史', { exact: true })).toHaveValue('')
  expect(requests.filter(request => request.endpoint === '/api/system/diagnostics/runs' && request.method === 'POST').length).toBe(startsBeforeReader)
  scenarios.push('existing-read-role-can-open-history-and-refresh-clears-a-record-no-longer-listed')
  await page.close(); role = 'SUPERADMIN'; mode = 'success'
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1.25 })
  page.on('pageerror', error => errors.push(error.message)); await page.route(url => url.pathname.startsWith('/api/'), routeApi)
  await page.goto(`${base}/tools`); await selectTrace(); await page.getByRole('button', { name: '开始诊断', exact: true }).click()
  await expect(page.locator('.diagnostic-progress')).toContainText('已完成 2 / 2 项检查')
  await checkLayout(); await screenshot('traceroute-results-1920-dsf125.png', 1.25)
  scenarios.push('1920-browser-device-scale-factor-1.25-keeps-route-table-readable')
  expect(requests.filter(request => request.method !== 'GET').every(request => request.endpoint === '/api/system/diagnostics/runs' || request.endpoint.endsWith('/cancel'))).toBe(true)
  expect(JSON.stringify(requests.filter(request => request.method === 'POST'))).not.toMatch(/password|credential|command|executablePath/)
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  result.status = 'PASS'; fs.rmSync(path.join(output, 'failure.png'), { force: true })
} catch (error) {
  failure = error; result.status = 'FAIL'; result.failure = error instanceof Error ? error.message : String(error)
  if (page && !page.isClosed()) {
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: false }).catch(() => {})
    console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: (await page.locator('body').innerText()).slice(0, 14000) }))
  }
} finally {
  try { await browser?.close(); result.resources.ownedChromeClosed = Boolean(browser) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.chromeCleanupError = String(error) }
  try { await server?.close(); result.resources.ownedViteClosed = Boolean(server) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.viteCleanupError = String(error) }
  result.completedAt = new Date().toISOString(); fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result))
}
if (failure) throw failure
