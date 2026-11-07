import { chromium } from '../web/node_modules/playwright/index.mjs'
import { existsSync, mkdirSync } from 'node:fs'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { workspacePath } from '../tools/workspace_paths.mjs'

const root = fileURLToPath(new URL('../src/NetConsole.Next.Service/wwwroot/', import.meta.url))
const output = process.env.NETCONSOLE_NEXT_UI_ARTIFACTS || workspacePath('build', 'r6-1-browser-e2e')
mkdirSync(output, { recursive: true })
const python = process.platform === 'win32' ? 'python' : 'python3'
const width = Number(process.env.NETCONSOLE_NEXT_UI_WIDTH || 1920)
const height = Number(process.env.NETCONSOLE_NEXT_UI_HEIGHT || 1080)
const port = Number(process.env.NETCONSOLE_NEXT_UI_PORT || (width === 2560 ? 4176 : 4174))
const server = spawn(python, ['-m', 'http.server', String(port), '--directory', root], { stdio: 'ignore' })
const base = `http://127.0.0.1:${port}`
for (let attempt = 0; attempt < 40; attempt += 1) {
  try { if ((await fetch(`${base}/`)).ok) break } catch { /* server is starting */ }
  await new Promise(resolve => setTimeout(resolve, 100))
}

const chromePath = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'
const browser = await chromium.launch({ headless: true, ...(existsSync(chromePath) ? { executablePath: chromePath } : {}), args: ['--no-sandbox'] })
const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 })
const pageErrors = []
page.on('pageerror', error => pageErrors.push(error.message))
let loggedIn = false
let restoreRequests = []
const json = (value, status = 200) => ({ status, headers: { 'content-type': 'application/json' }, body: JSON.stringify(value) })
const profile = { id: 'profile-b', host: '192.0.2.10', port: 22, username: 'reader', enabled: true, supportedProtocols: ['SSH'], terminalProtocol: 'SSH' }
const snapshot = { id: 'profile-b-v1', profileId: 'profile-b', deviceId: 'device-b', siteId: 'local', version: 1, state: 'SUCCEEDED', capturedMs: 1792771200000, byteCount: 128, sha256: 'fixture-sha', canDownload: true, configurationKind: 'RUNNING' }
async function goRoute(path) {
  await page.evaluate((target) => { history.pushState({}, '', target); window.dispatchEvent(new PopStateEvent('popstate')) }, path)
  await page.waitForTimeout(150)
}

await page.route('**/api/**', async route => {
  const request = route.request(); const url = new URL(request.url()); const path = url.pathname
  if (path === '/api/auth/me') return route.fulfill(loggedIn ? json({ username: 'superadmin', role: 'SUPERADMIN' }) : json({}, 401))
  if (path === '/api/auth/csrf') return route.fulfill(json({ csrfToken: 'fixture-csrf' }))
  if (path === '/api/auth/login') { loggedIn = true; return route.fulfill(json({ user: { username: 'superadmin', role: 'SUPERADMIN' }, csrfToken: 'fixture-csrf' })) }
  if (path === '/api/system/smart-config/templates') return route.fulfill(json([{ id: 'h3c-interface-description', name: 'H3C 接口描述', description: '为接口生成安全的描述配置预览。', variables: [{ name: 'interface', label: '接口名称', example: 'GigabitEthernet1/0/1', required: true }, { name: 'description', label: '描述', example: '车站上联', required: true }] }]))
  if (path === '/api/system/smart-config/preview') return route.fulfill(json({ previewId: 'smart-preview', templateId: 'h3c-interface-description', templateName: 'H3C 接口描述', state: 'PREVIEWED', executionMode: 'DRY_RUN_ONLY', deviceWriteAllowed: false, writeAttempted: false, commands: [{ index: 0, command: 'system-view', risk: 'WRITE_PREVIEW_ONLY' }, { index: 1, command: 'interface GigabitEthernet1/0/1', risk: 'WRITE_PREVIEW_ONLY' }], errors: [], createdMs: 1792771200000 }))
  if (path === '/api/system/devices' && request.method() === 'GET') return route.fulfill(json({ page: 1, pageSize: 100, total: 1, hasMore: false, summary: { totalRecords: 1, validDevices: 1, invalidRecords: 0, enabledDevices: 1, disabledDevices: 0, onlineDevices: 1, offlineDevices: 0, unknownStatus: 0 }, items: [{ id: 'device-b', deviceId: 'stable-b', name: '核心交换机', managementIp: '192.0.2.10', managementAddress: '192.0.2.10', port: 22, vendor: 'H3C', model: 'S5560', deviceType: 'SWITCH', protocol: 'SSH', site: '一号线', siteId: 'local', group: '核心', enabled: true, connectionStatus: 'ONLINE', credentialConfigured: true, hostKeyUpdatePolicy: 'AUTO', terminalEncoding: 'AUTO', isValid: true, qualityIssues: [], updatedAt: 1792771200000 }]}))
  if (path === '/api/system/h3c/profiles') return route.fulfill(json([profile]))
  if (path === '/api/system/h3c/profiles/profile-b/lldp-claims') return route.fulfill(json({ profileId: 'profile-b', scopeState: 'AUTHORIZED', isGlobal: false, claimsRevision: 'claims-1', allowedSiteReferences: ['local'], factCount: 1, relationCount: 1, relations: [{ profileId: 'profile-b', localInterface: 'GigabitEthernet1/0/1', neighborDevice: '接入交换机', neighborPort: 'GigabitEthernet1/0/24', neighborManagementAddress: '192.0.2.11', siteId: 'local', source: 'fixture', capturedMs: 1792771200000, factRevision: 'fact-1', relationKey: 'stable-b|1|neighbor', changeKind: 'UNCHANGED' }] }))
  if (path === '/api/system/modules') return route.fulfill(json([{ key: 'config-backups', enabled: true }]))
  if (path === '/api/system/devices/device-b') return route.fulfill(json({ item: { id: 'device-b', name: '核心交换机', managementIp: '192.0.2.10', managementAddress: '192.0.2.10', vendor: 'H3C', deviceType: 'SWITCH', terminalProfileId: 'profile-b' }, terminalProfileId: 'profile-b', terminalProfile: profile, recentSessions: [], auditEvents: [] }))
  if (path === '/api/system/config-backups' && request.method() === 'GET') return route.fulfill(json([snapshot, { ...snapshot, id: 'profile-b-v2', version: 2 }]))
  if (path === '/api/system/config-backups/restore/preview') return route.fulfill(json({ id: 'restore-preview', snapshotId: 'profile-b-v1', targetProfileId: 'profile-b', targetDeviceId: 'device-b', targetSiteId: 'local', configurationKind: 'RUNNING', sourceSha256: 'fixture-sha', currentSnapshotId: 'profile-b-v2', addedLines: 2, removedLines: 1, changedLines: 1, compatibility: 'PROFILE_EXACT_H3C', confirmationToken: 'fixture-token', expiresMs: 1792774800000, state: 'PREVIEW' }))
  if (path === '/api/system/config-backups/restore/execute') {
    const body = JSON.parse(request.postData() || '{}'); restoreRequests.push(body)
    return body.apply ? route.fulfill(json({ error: 'CONFIG_RESTORE_WRITE_ADAPTER_UNAVAILABLE', detail: 'writer disabled' }, 409)) : route.fulfill(json({ previewId: 'restore-preview', state: 'DRY_RUN', detail: '未向设备发送写命令', completedMs: 1792771200000 }))
  }
  return route.fulfill(json({}))
})

try {
  await page.goto(`${base}/`, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill('superadmin')
  await page.getByLabel('密码').fill('admin')
  await page.getByRole('button', { name: /登录/ }).click()
  await page.waitForURL(`${base}/`)

  await goRoute('/smart-config')
  await page.getByRole('heading', { name: '智能配置' }).waitFor()
  await page.getByRole('textbox', { name: /接口名称/ }).fill('GigabitEthernet1/0/1')
  await page.getByRole('textbox', { name: /描述/ }).fill('车站上联')
  await page.getByRole('button', { name: '生成命令预览' }).click()
  await page.getByText('DRY_RUN_ONLY').waitFor()
  await page.screenshot({ path: `${output}/smart-config-${width}x${height}.png`, fullPage: true })

  await goRoute('/topology')
  await page.getByRole('heading', { name: '拓扑与资产' }).waitFor()
  await page.getByText('接入交换机').waitFor()
  await page.screenshot({ path: `${output}/topology-${width}x${height}.png`, fullPage: true })

  await goRoute('/config-backups?deviceId=device-b')
  await page.getByRole('heading', { name: /核心交换机.*运行配置版本历史/ }).waitFor()
  await page.getByRole('button', { name: '恢复预览' }).first().click()
  await page.getByRole('heading', { name: '配置恢复预览' }).waitFor()
  await page.getByRole('button', { name: '执行 dry-run' }).click()
  await page.getByText('DRY_RUN').waitFor()
  await page.getByRole('button', { name: '执行恢复' }).click()
  await page.getByRole('region', { name: '配置恢复预览' }).getByText('设备未被修改', { exact: false }).waitFor()
  if (!restoreRequests.some(request => request.apply === false) || !restoreRequests.some(request => request.apply === true)) throw new Error('restore dry-run/apply requests were not both observed')
  if (pageErrors.length) throw new Error(`page errors: ${pageErrors.join(' | ')}`)
  await page.screenshot({ path: `${output}/config-restore-${width}x${height}.png`, fullPage: true })
  console.log(`R6_1_BROWSER_E2E=PASS (${width}x${height})`)
} finally {
  await browser.close()
  server.kill('SIGTERM')
}
