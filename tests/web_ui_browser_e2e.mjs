import { spawn } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const root = new URL('../src/NetConsole.Next.Service/wwwroot/', import.meta.url).pathname
const output = workspacePath('build', process.env.NETCONSOLE_NEXT_UI_ARTIFACTS || 'web-ui-acceptance')
mkdirSync(output, { recursive: true })
const server = spawn('python3', ['-m', 'http.server', '4173', '--directory', root], { stdio: 'ignore' })
const base = 'http://127.0.0.1:4173'
for (let attempt = 0; attempt < 40; attempt += 1) {
  try { if ((await fetch(`${base}/`)).ok) break } catch { /* server is starting */ }
  await new Promise((resolve) => setTimeout(resolve, 100))
}

const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
let loggedIn = false
await page.route('**/api/**', async (route) => {
  const request = route.request(); const url = new URL(request.url())
  if (url.pathname === '/api/auth/me') return route.fulfill({ status: loggedIn ? 200 : 401, body: loggedIn ? JSON.stringify({ username: 'admin', role: 'ADMIN' }) : '' , headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/auth/login') { loggedIn = true; return route.fulfill({ status: 200, body: JSON.stringify({ user: { username: 'admin', role: 'ADMIN' }, csrfToken: 'fixture-csrf' }), headers: { 'content-type': 'application/json' } }) }
  if (url.pathname === '/api/system/health') return route.fulfill({ status: 200, body: JSON.stringify({ status: 'RUNNING', serviceState: 'RUNNING', healthState: 'HEALTHY', collectionState: 'LISTENING', h3cCollectionEnabled: true, syslogEnabled: true, rawWriteAvailable: true, rawSegments: { finalized: 12, incomplete: 0, missing: 0 }, udpReceived: 1842, rawFlushedRecords: 1842, h3cMetrics: { activeSshSessions: 1, collectionFailures: 0 }, resources: { workingSetBytes: 73400320, threadCount: 18 }, instanceId: 'fixture-instance-01' }), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/tasks') return route.fulfill({ status: 200, body: JSON.stringify([]), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/host-tools') return route.fulfill({ status: 200, body: JSON.stringify([
    { id: 'securecrt', displayName: 'SecureCRT', product: 'SecureCRT', status: 'HOST_WINDOWS_REQUIRED', configured: false, isWindowsTool: true, detail: 'fixture: real host helper not connected' },
    { id: 'xshell', displayName: 'Xshell', product: 'Xshell', status: 'NOT_CONFIGURED', configured: false, isWindowsTool: true, detail: 'fixture: configure on host' },
    { id: 'putty', displayName: 'PuTTY', product: 'PuTTY', status: 'NOT_CONFIGURED', configured: false, isWindowsTool: true, detail: 'fixture: configure on host' }
  ]), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/files/roots') return route.fulfill({ status: 200, body: JSON.stringify([
    { id: 'raw', label: 'Raw 采集', path: 'raw', exists: true },
    { id: 'terminal-recordings', label: '终端录制', path: 'terminal-recordings', exists: true },
    { id: 'published', label: '已发布文件', path: 'published', exists: false }
  ]), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/files/list') return route.fulfill({ status: 200, body: JSON.stringify({ rootId: 'raw', relativePath: '', items: [], page: 1, pageSize: 100, total: 0, hasMore: false }), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/terminal/devices') return route.fulfill({ status: 200, body: JSON.stringify([{ id: 'fixture-router-01', host: '192.0.2.10', port: 22, username: 'reader', enabled: true, supportedProtocols: ['SSH', 'TELNET'], hostKeyUpdatePolicy: 'STRICT', allowLegacyAlgorithms: false, terminalEncoding: 'AUTO', terminalProtocol: 'AUTO', allowTelnetFallback: false }]), headers: { 'content-type': 'application/json' } })
  if (url.pathname === '/api/system/terminal/sessions') return route.fulfill({ status: 200, body: JSON.stringify({ items: [{ id: 'fixture-session-01', targetId: 'fixture-router-01', targetName: '192.0.2.10', protocol: 'SSH', state: 'DISCONNECTED', startedMs: 1792771200000, endedMs: 1792771260000, recordingStatus: 'COMPLETE', recordingComplete: true, commandCount: 2, outputBytes: 128, recordingBytes: 256 }], total: 1 }), headers: { 'content-type': 'application/json' } })
  if (url.pathname.endsWith('/events')) return route.fulfill({ status: 200, body: JSON.stringify([{ atMs: 1792771200000, eventType: 'CONNECTED', detailJson: '{"protocol":"SSH"}' }, { atMs: 1792771260000, eventType: 'DISCONNECTED', detailJson: '{"reason":"USER"}' }]), headers: { 'content-type': 'application/json' } })
  if (url.pathname.endsWith('/recording/integrity')) return route.fulfill({ status: 200, body: JSON.stringify({ status: 'COMPLETE', detail: 'fixture recording verified', hashMatches: true }), headers: { 'content-type': 'application/json' } })
  if (url.pathname.endsWith('/recording/replay')) return route.fulfill({ status: 200, body: JSON.stringify({ entries: [{ atMs: 1792771201000, direction: 'output', dataB64: 'Zml4dHVyZSByZWFkeT4g' }], hasMore: false }), headers: { 'content-type': 'application/json' } })
  return route.fulfill({ status: 200, body: JSON.stringify({}), headers: { 'content-type': 'application/json' } })
})

try {
  await page.goto(`${base}/`, { waitUntil: 'networkidle' })
  await page.screenshot({ path: `${output}/web-pr1-login.png`, fullPage: true })
  await page.locator('input[autocomplete="username"]').fill('admin')
  await page.locator('input[autocomplete="current-password"]').fill('admin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  await page.screenshot({ path: `${output}/web-pr1-overview-light.png`, fullPage: true })
  await page.getByTitle('切换深色主题').click()
  await page.screenshot({ path: `${output}/web-pr1-overview-dark.png`, fullPage: true })
  await page.getByRole('button', { name: '设备管理' }).click()
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.screenshot({ path: `${output}/web-pr1-devices.png`, fullPage: true })
  await page.getByRole('button', { name: '终端控制台' }).click()
  await page.getByRole('heading', { name: '终端控制台' }).waitFor()
  await page.screenshot({ path: `${output}/web-pr1-terminal.png`, fullPage: true })
  await page.locator('.history-item').first().click()
  await page.getByText('会话历史 / 只读回放').waitFor()
  await page.screenshot({ path: `${output}/web-pr1-history-replay.png`, fullPage: true })
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: '任务中心' }).click()
  await page.getByRole('heading', { name: '任务中心' }).waitFor()
  await page.screenshot({ path: `${output}/web-pr1-tasks.png`, fullPage: true })
  await page.getByRole('button', { name: '工具集' }).click()
  await page.getByRole('heading', { name: '工具集' }).waitFor()
  await page.screenshot({ path: output + '/web-pr2-tools.png', fullPage: true })
  await page.getByRole('button', { name: '文件管理' }).click()
  await page.getByRole('heading', { name: '文件管理' }).waitFor()
  await page.screenshot({ path: output + '/web-pr2-files.png', fullPage: true })
  console.log('PASS: Vue login, light/dark overview, devices, terminal, tasks, tools and files screenshots')
} finally {
  await browser.close()
  server.kill('SIGTERM')
}

