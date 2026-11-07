import { spawn } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { mkdtemp, rm } from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = fileURLToPath(new URL('..', import.meta.url))
const serviceDll = path.join(repo, 'src', 'NetConsole.Next.Service', 'bin', 'Release', 'net10.0', 'NetConsole.Next.Service.dll')
const output = process.env.NETCONSOLE_NEXT_UI_ARTIFACTS || workspacePath('build', 'web-service-browser')
mkdirSync(output, { recursive: true })

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer()
    server.once('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const address = server.address()
      server.close(() => resolve(address.port))
    })
  })
}

async function waitForHealth(base, service) {
  for (let attempt = 0; attempt < 150; attempt += 1) {
    if (service.exitCode !== null) throw new Error(`service exited before readiness: ${service.exitCode}`)
    try {
      const response = await fetch(`${base}/api/system/health`)
      if (response.ok) return
    } catch { /* service is starting */ }
    await new Promise(resolve => setTimeout(resolve, 100))
  }
  throw new Error('service did not become ready')
}

async function stopService(service) {
  if (!service.pid) return
  if (service.exitCode !== null) return
  if (service.pid && service.exitCode === null) service.kill('SIGTERM')
  await Promise.race([
    new Promise(resolve => service.once('exit', resolve)),
    new Promise(resolve => setTimeout(resolve, 15000)),
  ])
  if (service.pid && service.exitCode === null) service.kill('SIGKILL')
}

function waitForSpawn(service) {
  return new Promise((resolve, reject) => {
    service.once('spawn', resolve)
    service.once('error', reject)
  })
}

const dataParent = workspacePath('test-data')
mkdirSync(dataParent, { recursive: true })
let browser = null
let dataRoot = null
let service = null
let page = null
const apiFailures = []
const pageErrors = []
let authenticated = false

async function apiJson(pathname, options = {}) {
  return page.evaluate(async ({ pathname, options }) => {
    const response = await fetch(pathname, { credentials: 'include', ...options })
    const text = await response.text()
    let body = null
    try { body = text ? JSON.parse(text) : null } catch { body = text }
    return { status: response.status, body }
  }, { pathname, options })
}

try {
  const port = await freePort()
  dataRoot = await mkdtemp(path.join(dataParent, 'web-service-browser-'))
  const base = `http://127.0.0.1:${port}`
  service = spawn('dotnet', [serviceDll], {
    // The published static assets live beside the DLL; using the repository as
    // content root would make ASP.NET look for a non-existent root `wwwroot`.
    cwd: path.dirname(serviceDll),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Production',
      NETCONSOLE_NEXT_DATA_ROOT: dataRoot,
      NETCONSOLE_NEXT_WEB_PORT: String(port),
      NETCONSOLE_NEXT_SYSLOG_PORT: '0',
      NETCONSOLE_NEXT_H3C_ENABLED: '0',
      NETCONSOLE_NEXT_TERMINAL_ENABLED: '0',
    },
    stdio: 'ignore',
    windowsHide: true,
  })
  await waitForSpawn(service)
  browser = await chromium.launch({
    headless: true,
    ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : process.platform === 'win32' ? { executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' } : {}),
    args: ['--no-sandbox'],
  })
  page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('response', response => {
    const responseUrl = new URL(response.url())
    const optionalSiteJumpHostLookup = response.status() === 404 &&
      /^\/api\/system\/sites\/[^/]+\/ssh-jump-host$/.test(responseUrl.pathname)
    if (responseUrl.pathname.includes('/api/') && response.status() >= 400 &&
        !(responseUrl.pathname === '/api/auth/me' && !authenticated) &&
        !optionalSiteJumpHostLookup)
      apiFailures.push(`${response.status()} ${response.url()}`)
  })

  await waitForHealth(base, service)
  // The service serves the SPA entry at `/`; Vue Router handles the initial
  // `/login` navigation client-side after the unauthenticated probe.
  await page.goto(`${base}/`, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill('superadmin')
  await page.getByLabel('密码').fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  authenticated = true

  const csrfResponse = await apiJson('/api/auth/csrf')
  if (csrfResponse.status !== 200 || !csrfResponse.body?.csrfToken) throw new Error(`CSRF API failed: ${JSON.stringify(csrfResponse)}`)
  const syntheticDevice = {
    name: 'Service E2E Fixture Device', managementIp: '192.0.2.123', vendor: 'H3C',
    model: 'S5560-E2E', deviceType: 'SWITCH', siteId: 'local', enabled: true,
    description: 'isolated service-backed browser fixture', terminalProfileId: null, groupId: null,
  }
  const createResponse = await apiJson('/api/system/devices', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-NC-CSRF': csrfResponse.body.csrfToken },
    body: JSON.stringify(syntheticDevice),
  })
  if (createResponse.status !== 201 || !createResponse.body?.item?.id)
    throw new Error(`device create API failed: ${JSON.stringify(createResponse)}`)
  const deviceId = createResponse.body.item.id
  const detailResponse = await apiJson(`/api/system/devices/${encodeURIComponent(deviceId)}`)
  if (detailResponse.status !== 200 || detailResponse.body?.item?.managementIp !== syntheticDevice.managementIp)
    throw new Error(`device detail persistence failed: ${JSON.stringify(detailResponse)}`)
  const smartResponse = await apiJson('/api/system/smart-config/preview', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'X-NC-CSRF': csrfResponse.body.csrfToken },
    body: JSON.stringify({ templateId: 'h3c-interface-description', siteId: 'local', variables: {
      interface: 'GigabitEthernet1/0/1', description: 'service E2E fixture',
    } }),
  })
  if (smartResponse.status !== 200 || smartResponse.body?.executionMode !== 'DRY_RUN_ONLY' || smartResponse.body?.deviceWriteAllowed !== false)
    throw new Error(`smart config API failed: ${JSON.stringify(smartResponse)}`)
  await page.screenshot({ path: path.join(output, 'service-overview-1920.png'), fullPage: true })

  for (const label of ['设备管理', '任务中心', '系统设置']) {
    await page.locator('.app-sidebar').getByRole('button', { name: label }).click()
    await page.getByRole('heading', { name: label }).waitFor()
    await page.locator('.card').first().waitFor()
  }
  await page.getByRole('heading', { name: '模块开放状态' }).waitFor()
  await page.locator('.app-sidebar').getByRole('button', { name: '设备管理' }).click()
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.getByText('Service E2E Fixture Device').waitFor()
  await page.locator('.app-sidebar').getByRole('button', { name: '工具集' }).click()
  await page.getByRole('heading', { name: '工具集' }).waitFor()
  await page.locator('.tool-card, .tool-list, .empty-state').first().waitFor()
  await page.locator('.app-sidebar').getByRole('button', { name: '文件管理' }).click()
  await page.getByRole('heading', { name: '文件管理' }).waitFor()
  await page.getByRole('heading', { name: 'Raw 采集' }).waitFor()
  await page.screenshot({ path: path.join(output, 'service-files-1920.png'), fullPage: true })

  if (apiFailures.length || pageErrors.length) {
    throw new Error(`service browser diagnostics: api=${apiFailures.join(' | ')} page=${pageErrors.join(' | ')}`)
  }
  console.log('PASS: real service-backed browser E2E at 1920x1080 (login, device create/detail persistence, SmartConfig dry-run, overview, tasks, tools, files)')
} finally {
  try {
    if (browser) await browser.close()
  } finally {
    try {
      if (service) await stopService(service)
    } finally {
      if (dataRoot) {
        try {
          await rm(dataRoot, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 })
        } catch (error) {
          console.error(`WARNING: isolated service data cleanup failed at ${dataRoot}: ${error}`)
        }
      }
    }
  }
}
