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
const browser = await chromium.launch({
  headless: true,
  ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : process.platform === 'win32' ? { executablePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' } : {}),
  args: ['--no-sandbox'],
})

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
  if (service.exitCode === null) service.kill('SIGTERM')
  await Promise.race([
    new Promise(resolve => service.once('exit', resolve)),
    new Promise(resolve => setTimeout(resolve, 15000)),
  ])
  if (service.exitCode === null) service.kill('SIGKILL')
}

const port = await freePort()
const dataParent = workspacePath('test-data')
mkdirSync(dataParent, { recursive: true })
const dataRoot = await mkdtemp(path.join(dataParent, 'web-service-browser-'))
const base = `http://127.0.0.1:${port}`
const service = spawn('dotnet', [serviceDll], {
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

const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
const apiFailures = []
const pageErrors = []
page.on('pageerror', error => pageErrors.push(error.message))
page.on('response', response => {
  if (response.url().includes('/api/') && response.status() >= 500) apiFailures.push(`${response.status()} ${response.url()}`)
})

try {
  await waitForHealth(base, service)
  // The service serves the SPA entry at `/`; Vue Router handles the initial
  // `/login` navigation client-side after the unauthenticated probe.
  await page.goto(`${base}/`, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill('superadmin')
  await page.getByLabel('密码').fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  await page.screenshot({ path: path.join(output, 'service-overview-1920.png'), fullPage: true })

  for (const label of ['设备管理', '任务中心', '系统设置']) {
    await page.locator('.app-sidebar').getByRole('button', { name: label }).click()
    await page.getByRole('heading', { name: label }).waitFor()
    await page.locator('.card').first().waitFor()
  }
  await page.getByRole('heading', { name: '模块开放状态' }).waitFor()
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
  console.log('PASS: real service-backed browser smoke at 1920x1080 (login, overview, devices, tasks, tools, files)')
} finally {
  await browser.close()
  await stopService(service)
  try {
    await rm(dataRoot, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 })
  } catch (error) {
    console.error(`WARNING: isolated service data cleanup failed at ${dataRoot}: ${error}`)
  }
}
