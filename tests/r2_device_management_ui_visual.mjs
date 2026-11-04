import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { mkdirSync } from 'node:fs'
import path from 'node:path'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const root = fileURLToPath(new URL('../src/NetConsole.Next.Service/wwwroot/', import.meta.url))
const output = process.env.NETCONSOLE_R2_UI_ARTIFACTS || workspacePath('build', 'ui-r2-device-management', 'baseline')
const port = Number(process.env.NETCONSOLE_R2_UI_PORT || 4174)
const base = `http://127.0.0.1:${port}`
mkdirSync(output, { recursive: true })

const devices = [
  { id: 'dev-local-core-01', name: '核心交换机 01', managementAddress: '192.0.2.10', managementIp: '192.0.2.10', port: 22, username: 'ops', vendor: 'H3C', model: 'S5560X', deviceType: 'SWITCH', protocol: 'SSH', site: 'Local Site', siteId: 'local', group: '核心网络', groupId: 'group-core', enabled: true, connectionStatus: 'CONNECTED', credentialConfigured: true, hostKeyUpdatePolicy: 'STRICT', terminalEncoding: 'AUTO', isValid: true, qualityIssues: [], deviceId: 'dev-local-core-01', updatedAt: 1792771260000, terminalProfileId: 'profile-core-01' },
  { id: 'dev-local-ap-01', name: '站台 AP 01', managementAddress: '192.0.2.20', managementIp: '192.0.2.20', port: 22, username: 'reader', vendor: 'H3C', model: 'WA6638', deviceType: 'FIT_AP', protocol: 'SSH', site: 'Local Site', siteId: 'local', group: '站台无线', groupId: 'group-wireless', enabled: true, connectionStatus: 'DISCONNECTED', credentialConfigured: true, hostKeyUpdatePolicy: 'STRICT', terminalEncoding: 'AUTO', isValid: true, qualityIssues: [], deviceId: 'dev-local-ap-01', updatedAt: 1792771200000, terminalProfileId: null },
  { id: 'dev-local-zte-01', name: '传输设备 01', managementAddress: '192.0.2.30', managementIp: '192.0.2.30', port: 22, username: 'ops', vendor: 'ZTE', model: 'ZXA10', deviceType: 'NETWORK_DEVICE', protocol: 'SSH', site: 'Local Site', siteId: 'local', group: '传输接入', groupId: 'group-transport', enabled: false, connectionStatus: 'DISABLED', credentialConfigured: false, hostKeyUpdatePolicy: '—', terminalEncoding: '—', isValid: true, qualityIssues: [], deviceId: 'dev-local-zte-01', updatedAt: 1792771100000, terminalProfileId: null },
]
const groups = [
  { id: 'group-core', siteId: 'local', name: '核心网络', description: '核心交换与汇聚设备', createdMs: 1792770000000, updatedMs: 1792770000000, deviceCount: 1 },
  { id: 'group-wireless', siteId: 'local', name: '站台无线', description: 'AC 管理的 FIT-AP 资产', createdMs: 1792770000000, updatedMs: 1792770000000, deviceCount: 1 },
  { id: 'group-transport', siteId: 'local', name: '传输接入', description: '传输侧普通网络设备', createdMs: 1792770000000, updatedMs: 1792770000000, deviceCount: 1 },
]
const profiles = [{ id: 'profile-core-01', host: '192.0.2.10', port: 22, username: 'ops', enabled: true, supportedProtocols: ['SSH'], hostKeyUpdatePolicy: 'STRICT', allowLegacyAlgorithms: false, terminalEncoding: 'AUTO', terminalProtocol: 'SSH', allowTelnetFallback: false }]

function json(status, body) {
  return { status, body: JSON.stringify(body), headers: { 'content-type': 'application/json' } }
}

const server = spawn('python', ['-m', 'http.server', String(port), '--directory', root], { stdio: 'ignore', windowsHide: true })
for (let attempt = 0; attempt < 50; attempt += 1) {
  try { if ((await fetch(`${base}/`)).ok) break } catch { /* server is starting */ }
  await new Promise(resolve => setTimeout(resolve, 100))
}

let loggedIn = false
let authenticated = false
const browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
const page = await context.newPage()
const consoleErrors = []
const pageErrors = []
page.on('console', message => { if (authenticated && message.type() === 'error') consoleErrors.push(message.text()) })
page.on('pageerror', error => { if (authenticated) pageErrors.push(error.message) })
await page.route('**/api/**', async route => {
  const request = route.request()
  const url = new URL(request.url())
  if (url.pathname === '/api/auth/me') return route.fulfill(loggedIn ? json(200, { username: 'superadmin', role: 'SUPERADMIN' }) : json(401, {}))
  if (url.pathname === '/api/auth/login') { loggedIn = true; return route.fulfill(json(200, { user: { username: 'superadmin', role: 'SUPERADMIN' }, csrfToken: 'r2-fixture-csrf' })) }
  if (url.pathname === '/api/system/health') return route.fulfill(json(200, { status: 'RUNNING', serviceState: 'RUNNING', healthState: 'HEALTHY', collectionState: 'LISTENING', h3cCollectionEnabled: true, syslogEnabled: false, rawWriteAvailable: true, rawSegments: { finalized: 8, incomplete: 0, missing: 0 }, udpReceived: 812, rawFlushedRecords: 812, h3cMetrics: { activeSshSessions: 1, collectionFailures: 0 }, resources: { workingSetBytes: 73400320, threadCount: 18 }, instanceId: 'r2-visual-fixture' }))
  if (url.pathname === '/api/system/device-sites') return route.fulfill(json(200, [{ id: 'local', name: 'Local Site' }]))
  if (url.pathname === '/api/system/devices' && request.method() === 'GET') {
    const search = (url.searchParams.get('search') || '').toLowerCase()
    const status = url.searchParams.get('status') || ''
    const groupId = url.searchParams.get('groupId') || ''
    const filtered = devices.filter(device => (!search || [device.id, device.name, device.managementIp, device.vendor, device.model].some(value => value.toLowerCase().includes(search))) && (!groupId || device.groupId === groupId) && (!status || (status === 'ENABLED' && device.enabled) || (status === 'DISABLED' && !device.enabled) || (status === 'ONLINE' && device.connectionStatus === 'CONNECTED') || (status === 'OFFLINE' && device.connectionStatus === 'DISCONNECTED') || (status === 'UNKNOWN' && device.connectionStatus === 'UNKNOWN')))
    return route.fulfill(json(200, { page: Number(url.searchParams.get('page') || 1), pageSize: Number(url.searchParams.get('pageSize') || 20), total: filtered.length, hasMore: false, summary: { totalRecords: filtered.length, validDevices: filtered.length, invalidRecords: 0, enabledDevices: devices.filter(device => device.enabled).length, disabledDevices: devices.filter(device => !device.enabled).length, onlineDevices: devices.filter(device => device.connectionStatus === 'CONNECTED').length, offlineDevices: devices.filter(device => device.connectionStatus === 'DISCONNECTED').length, unknownStatus: 0 }, items: filtered }))
  }
  if (url.pathname === '/api/system/device-groups') return route.fulfill(json(200, groups))
  if (url.pathname === '/api/system/terminal/devices') return route.fulfill(json(200, profiles))
  if (url.pathname.startsWith('/api/system/devices/') && request.method() === 'GET') {
    const id = decodeURIComponent(url.pathname.split('/').pop())
    const item = devices.find(device => device.id === id) || devices[0]
    return route.fulfill(json(200, { item, description: 'R2 visual fixture', createdMs: 1792770000000, updatedMs: item.updatedAt, terminalProfileId: item.terminalProfileId, terminalProfile: null, recentSessions: [], auditEvents: [] }))
  }
  if (url.pathname === '/api/system/tasks') return route.fulfill(json(200, []))
  return route.fulfill(json(200, {}))
})

async function settle() {
  await page.waitForLoadState('domcontentloaded')
  await page.waitForTimeout(450)
}
async function capture(name, viewport, deviceScaleFactor = 1) {
  await page.setViewportSize(viewport)
  await page.goto(`${base}/`, { waitUntil: 'domcontentloaded' })
  await settle()
  await page.locator('button.nav-item').filter({ hasText: '设备管理' }).click()
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await settle()
  await page.screenshot({ path: path.join(output, name), fullPage: true })
  return { name, cssViewport: viewport, requestedDeviceScaleFactor: deviceScaleFactor, actualDeviceScaleFactor: await page.evaluate(() => window.devicePixelRatio), deviceRows: await page.locator('.data-table tbody .device-name').count() }
}

try {
  await page.goto(`${base}/`, { waitUntil: 'domcontentloaded' })
  await page.locator('input[autocomplete="username"]').fill('superadmin')
  await page.locator('input[autocomplete="current-password"]').fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.waitForURL(`${base}/`)
  authenticated = true
  await settle()
  const captures = []
  captures.push(await capture('devices-1920x1080.png', { width: 1920, height: 1080 }))
  await page.getByRole('button', { name: '新增设备' }).click()
  const createDialog = page.getByRole('dialog', { name: '新增设备' })
  await createDialog.waitFor()
  await createDialog.screenshot({ path: path.join(output, 'device-create-dialog-1920x1080.png') })
  await createDialog.getByRole('button', { name: '取消' }).click()
  await page.locator('tbody tr').filter({ hasText: '核心交换机 01' }).getByRole('button', { name: '详情' }).click()
  const detailDialog = page.getByRole('dialog', { name: '设备详情 / 审计' })
  await detailDialog.waitFor()
  await detailDialog.screenshot({ path: path.join(output, 'device-detail-dialog-1920x1080.png') })
  await page.keyboard.press('Escape')
  captures.push(await capture('devices-2560x1440.png', { width: 2560, height: 1440 }))
  captures.push(await capture('devices-windows-125pct-simulated.png', { width: 1536, height: 864 }, 1.25))
  const report = { base, root, captures, consoleErrors, pageErrors }
  console.log(JSON.stringify(report, null, 2))
  if (consoleErrors.length || pageErrors.length) process.exitCode = 1
} finally {
  await browser.close()
  server.kill()
}
