// Owned source Vite + Chrome and synthetic API responses; no service/device calls.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import net from 'node:net'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'terminal-history-pagination-browser')
fs.mkdirSync(output, { recursive: true })
const scenarios = []; const screenshots = []; const requests = []; const errors = []; const unexpected = []
const viewports = [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]
const report = { status: 'RUNNING', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING',
  backendExercised: false, deviceConnections: 0, databaseWrites: 0, fixtureSessions: 27,
  startedAt: new Date().toISOString(), viewports, deviceScaleFactor: 1, screenshotMode: 'VIEWPORT',
  scenarios, screenshots, requests, errors, unexpected, resources: { vitePort: null, ownedViteClosed: false, ownedChromeClosed: false } }
let server; let browser; let page; let failure
let historyFailure = false; let replayFailure = false; let historyDenied = false; let replayDenied = false
const sessions = Array.from({ length: 27 }, (_, offset) => {
  const number = String(offset + 1).padStart(3, '0')
  return { id: `fixture-session-${number}`, targetId: `fixture-device-${number}`, targetName: `历史交换机 ${number} · 合成样本`,
    protocol: offset % 3 === 0 ? 'TELNET' : 'SSH', state: 'CLOSED', startedMs: 1_800_000_000_000 - offset * 60000,
    recordingStatus: 'COMPLETE', recordingComplete: true, commandCount: 1, outputBytes: 30, recordingBytes: 160 }
})
const dock = () => page.getByRole('region', { name: '终端历史与回放', exact: true })
const rows = () => dock().getByTestId('terminal-history-row')
const dialog = () => page.getByRole('dialog', { name: '会话历史 / 只读回放', exact: true })
const historyReads = () => requests.filter(item => item.endpoint === '/api/system/terminal/sessions')
async function routeApi(route) {
  const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
  requests.push({ method: request.method(), endpoint, query: url.search })
  let status = 200; let body
  if (request.method() !== 'GET') { status = 405; body = {}; unexpected.push(`${request.method()} ${endpoint}`) }
  else if (endpoint === '/api/auth/me') body = { username: 'fixture-operator', role: 'ADMIN' }
  else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-terminal-history-csrf' }
  else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-terminal-history-ui' }
  else if (endpoint === '/api/system/modules') body = ['terminal', 'tasks', 'linux', 'devices'].map(key => ({ key, enabled: true }))
  else if (endpoint === '/api/system/devices') body = { items: [], total: 0, page: 1, pageSize: 1, summary: { totalRecords: 0, onlineDevices: 0 } }
  else if (['/api/system/terminal/devices', '/api/system/linux/servers', '/api/system/tasks', '/api/system/h3c/profiles'].includes(endpoint)) body = []
  else if (endpoint === '/api/system/terminal/sessions') {
    if (historyDenied) { status = 403; body = { error: 'FORBIDDEN', detail: 'private-scope-detail' } }
    else if (historyFailure) { historyFailure = false; status = 503; body = { error: 'UNAVAILABLE', detail: 'private-store-detail' } }
    else {
      const number = Number(url.searchParams.get('page') || 1); const size = Number(url.searchParams.get('pageSize') || 20)
      const keyword = (url.searchParams.get('keyword') || '').toLowerCase(); const protocol = url.searchParams.get('protocol')
      const filtered = sessions.filter(item => (!keyword || `${item.id} ${item.targetName}`.toLowerCase().includes(keyword)) && (!protocol || item.protocol === protocol))
      body = { page: number, pageSize: size, total: filtered.length, items: filtered.slice((number - 1) * size, number * size) }
    }
  } else if (endpoint.startsWith('/api/system/terminal/sessions/')) {
    const segments = endpoint.split('/'); const id = decodeURIComponent(segments[5]); const session = sessions.find(item => item.id === id)
    if (replayDenied) { status = 403; body = { error: 'FORBIDDEN', detail: 'private-replay-scope-detail' } }
    else if (!session) { status = 404; body = {} }
    else if (endpoint.endsWith('/events')) body = [{ atMs: session.startedMs, eventType: 'CLOSED', detailJson: `合成会话事件 ${id}` }]
    else if (endpoint.endsWith('/recording/integrity')) body = { status: 'COMPLETE', detail: 'synthetic-fixture-only', hashMatches: true }
    else if (endpoint.endsWith('/recording/replay')) {
      if (replayFailure) { replayFailure = false; status = 503; body = { error: 'REPLAY_UNAVAILABLE', detail: 'private-recording-detail' } }
      else {
        const offset = Number(url.searchParams.get('offset') || 0)
        body = { entries: [{ atMs: offset + 1, direction: 'output', dataB64: Buffer.from(offset ? `只读回显 ${id}\n` : `合成录制 ${id}\ndisplay version\n`).toString('base64') }], hasMore: offset === 0 }
      }
    } else if (endpoint.endsWith('/export')) {
      await route.fulfill({ status: 200, contentType: 'text/plain; charset=utf-8', body: `synthetic recording ${id}\n` }); return
    } else { status = 404; body = {}; unexpected.push(`${request.method()} ${endpoint}`) }
  } else { status = 404; body = {}; unexpected.push(`${request.method()} ${endpoint}`) }
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) })
}
async function screenshot(name) {
  await page.screenshot({ path: path.join(output, name), fullPage: false, animations: 'disabled' })
  screenshots.push({ file: name, viewport: page.viewportSize() })
}
async function checkLayout() {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true)
  const bounds = await dock().boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(0)
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(page.viewportSize().width + 1)
  expect(await dock().evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
  for (const control of await dock().locator('.terminal-history-filters input, .terminal-history-filters select, .terminal-history-filters button').all()) {
    const box = await control.boundingBox(); expect(box.x).toBeGreaterThanOrEqual(bounds.x)
    expect(box.x + box.width).toBeLessThanOrEqual(bounds.x + bounds.width + 1)
  }
}
async function openSession(number) {
  await rows().filter({ hasText: `历史交换机 ${String(number).padStart(3, '0')}` }).getByRole('button', { name: '回放 / 导出', exact: true }).click()
  await expect(dialog()).toBeVisible()
}
async function closeReplay() { await dialog().getByRole('button', { name: 'Close this dialog', exact: true }).click(); await expect(dialog()).toHaveCount(0) }
async function sessionsTab() { await dock().getByRole('button', { name: '会话历史', exact: true }).click() }
async function availablePort() {
  const listener = net.createServer()
  await new Promise((resolve, reject) => { listener.once('error', reject); listener.listen(0, '127.0.0.1', resolve) })
  const port = listener.address().port
  await new Promise((resolve, reject) => listener.close(error => error ? reject(error) : resolve()))
  return port
}
try {
  server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: await availablePort(), strictPort: true } })
  await server.listen(); report.resources.vitePort = server.httpServer.address().port
  const base = `http://127.0.0.1:${report.resources.vitePort}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1 })
  page.on('pageerror', error => errors.push(error.message)); await page.route(url => url.pathname.startsWith('/api/'), routeApi)
  await page.goto(`${base}/terminal`)
  await expect(dock()).toBeVisible()
  await expect(rows()).toHaveCount(10); await expect(dock()).toContainText('共 27 条 · 第 1 / 3 页')
  expect(new URLSearchParams(historyReads().at(-1).query).get('pageSize')).toBe('10')
  expect(requests.every(item => item.method === 'GET')).toBe(true)
  scenarios.push('27-fixture-sessions-load-ten-rows-with-server-pagination-and-no-live-session-creation')

  await openSession(8)
  await expect(dialog()).toContainText('合成会话事件 fixture-session-008')
  await expect(dialog()).toContainText('SHA-256 匹配')
  await dialog().getByRole('button', { name: '播放', exact: true }).click()
  await expect(dialog().locator('.history-replay')).toContainText('只读回显 fixture-session-008')
  const replayReads = requests.filter(item => item.endpoint.endsWith('/fixture-session-008/recording/replay'))
  expect(replayReads.map(item => new URLSearchParams(item.query).get('offset'))).toEqual(['0', '1'])
  await screenshot('terminal-history-older-replay-1920.png')
  const downloadReady = page.waitForEvent('download'); await dialog().getByRole('button', { name: 'TXT', exact: true }).click()
  const download = await downloadReady; expect(download.suggestedFilename()).toBe('terminal-fixture-session-008.txt')
  await download.saveAs(path.join(output, download.suggestedFilename()))
  expect(fs.readFileSync(path.join(output, download.suggestedFilename()), 'utf8')).toContain('synthetic recording fixture-session-008')
  scenarios.push('eighth-row-is-reachable-and-replays-all-offsets-then-exports-the-exact-selected-session')

  await closeReplay()
  await expect(dock().getByRole('button', { name: '终端回放', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(dock()).toContainText('历史交换机 008')
  await screenshot('terminal-history-replay-selection-1920.png')
  await dock().getByRole('button', { name: '打开回放 / 导出', exact: true }).click(); await expect(dialog()).toContainText('SHA-256 匹配')
  await closeReplay(); await sessionsTab()
  scenarios.push('replay-tab-retains-the-selected-session-and-reopens-the-real-replay-dialog')

  await dock().getByRole('button', { name: '下一页', exact: true }).click(); await expect(rows().first()).toContainText('历史交换机 011')
  await dock().getByRole('button', { name: '下一页', exact: true }).click(); await expect(rows()).toHaveCount(7)
  await expect(rows().last()).toContainText('历史交换机 027')
  await expect(dock().getByRole('button', { name: '下一页', exact: true })).toBeDisabled()
  await openSession(27); await expect(dialog()).toContainText('合成会话事件 fixture-session-027'); await closeReplay(); await sessionsTab()
  await dock().getByRole('button', { name: '上一页', exact: true }).click(); await expect(rows().first()).toContainText('历史交换机 011')
  scenarios.push('next-and-previous-reach-all-three-pages-and-the-oldest-session-27-remains-replayable')

  await page.getByLabel('搜索会话历史', { exact: true }).fill('027')
  await dock().getByRole('button', { name: '搜索历史', exact: true }).click()
  await expect(rows()).toHaveCount(1); await expect(rows()).toContainText('历史交换机 027')
  expect(Object.fromEntries(new URLSearchParams(historyReads().at(-1).query))).toMatchObject({ page: '1', keyword: '027' })
  await page.getByLabel('搜索会话历史', { exact: true }).fill('')
  await page.getByLabel('历史协议', { exact: true }).selectOption('TELNET')
  await expect(rows()).toHaveCount(9); await expect(dock()).toContainText('共 9 条 · 第 1 / 1 页')
  expect(await rows().locator('td:nth-child(2)').allTextContents()).toEqual(Array(9).fill('TELNET'))
  await page.getByLabel('历史协议', { exact: true }).selectOption('')
  await page.getByLabel('每页会话数', { exact: true }).selectOption('20')
  await expect(rows()).toHaveCount(20); await expect(dock()).toContainText('共 27 条 · 第 1 / 2 页')
  await page.getByLabel('每页会话数', { exact: true }).selectOption('50')
  await expect(rows()).toHaveCount(27); await expect(dock()).toContainText('共 27 条 · 第 1 / 1 页')
  await page.getByLabel('每页会话数', { exact: true }).selectOption('10'); await expect(rows()).toHaveCount(10)
  scenarios.push('keyword-and-protocol-filter-on-the-server-and-page-size-10-20-50-reset-to-page-one')

  for (const viewport of viewports) {
    await page.setViewportSize(viewport); await dock().scrollIntoViewIfNeeded(); await checkLayout()
    await screenshot(`terminal-history-pagination-${viewport.width}.png`)
  }
  scenarios.push('history-filter-and-pagination-controls-fit-1920x1080-and-2560x1440-without-horizontal-overflow')

  historyFailure = true
  await dock().getByRole('button', { name: '下一页', exact: true }).click()
  await expect(dock().getByRole('alert')).toContainText('会话历史读取失败'); await expect(rows()).toHaveCount(0)
  await expect(dock().getByRole('status')).toHaveText('历史页码暂不可用')
  await screenshot('terminal-history-page-error.png')
  await dock().getByRole('button', { name: '重试历史', exact: true }).click()
  await expect(rows().first()).toContainText('历史交换机 011'); await expect(dock().getByRole('alert')).toHaveCount(0)
  expect(new URLSearchParams(historyReads().at(-1).query).get('page')).toBe('2')
  scenarios.push('failed-page-clears-old-rows-and-retry-reads-the-same-page')

  replayFailure = true; await openSession(11)
  await expect(dialog().getByRole('alert')).toContainText('会话回放读取失败')
  await expect(dialog().getByRole('button', { name: 'TXT', exact: true })).toBeDisabled()
  await expect(dialog()).not.toContainText('合成会话事件 fixture-session-008')
  await dialog().getByRole('button', { name: '重试回放', exact: true }).click()
  await expect(dialog()).toContainText('合成会话事件 fixture-session-011'); await expect(dialog().getByRole('alert')).toHaveCount(0)
  await dialog().getByRole('button', { name: '播放', exact: true }).click()
  await expect(dialog().locator('.history-replay')).toContainText('只读回显 fixture-session-011')
  scenarios.push('replay-failure-clears-stale-events-blocks-export-and-can-retry-the-selected-session')

  await closeReplay(); historyDenied = true
  await dock().getByRole('button', { name: '刷新历史', exact: true }).click()
  await expect(dock().getByRole('alert')).toContainText('当前账户无权访问')
  await expect(dock().getByRole('button', { name: '打开回放 / 导出', exact: true })).toHaveCount(0)
  await expect(dock()).not.toContainText('历史交换机 011'); await sessionsTab(); await expect(rows()).toHaveCount(0)
  await screenshot('terminal-history-permission-revoked.png')
  historyDenied = false; await dock().getByRole('button', { name: '重试历史', exact: true }).click(); await expect(rows()).toHaveCount(10)
  replayDenied = true; await rows().first().getByRole('button', { name: '回放 / 导出', exact: true }).click()
  await expect(dialog()).toHaveCount(0); await expect(dock().getByRole('alert')).toContainText('当前账户无权访问')
  await sessionsTab(); await expect(rows()).toHaveCount(0)
  await expect(dock().getByRole('button', { name: '打开回放 / 导出', exact: true })).toHaveCount(0)
  replayDenied = false; await dock().getByRole('button', { name: '重试历史', exact: true }).click(); await expect(rows()).toHaveCount(10)
  expect(await page.locator('body').innerText()).not.toMatch(/private-(scope|store|recording|replay)/)
  scenarios.push('history-and-replay-permission-denials-clear-list-selected-session-and-dialog-without-private-server-detail')

  await dock().getByRole('button', { name: '任务记录 ↗', exact: true }).click()
  await expect(page).toHaveURL(`${base}/tasks`); await expect(page.getByRole('heading', { name: '任务中心', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '终端控制台', exact: true }).first().click()
  await expect(page).toHaveURL(`${base}/terminal`); await expect(rows()).toHaveCount(10)
  await dock().getByRole('button', { name: '终端回放', exact: true }).click()
  await expect(dock()).toContainText('请从会话历史选择一条记录')
  scenarios.push('task-record-entry-opens-the-real-task-center-and-returning-clears-cached-replay-selection')

  expect(requests.every(item => item.method === 'GET')).toBe(true)
  expect(requests.some(item => item.endpoint.endsWith('/ticket') || item.endpoint.endsWith('/stream'))).toBe(false)
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
  report.completedAt = new Date().toISOString(); fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify(report))
}
if (failure) throw failure
