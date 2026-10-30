import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = fileURLToPath(new URL('..', import.meta.url))
const base = process.env.NETCONSOLE_E2E_BASE_URL || 'http://127.0.0.1:18773'
const dataRoot = process.env.NETCONSOLE_E2E_DATA_ROOT
if (!dataRoot) throw new Error('NETCONSOLE_E2E_DATA_ROOT must point to the isolated browser data root')
const output = process.env.NETCONSOLE_P2A_BROWSER_OUTPUT || workspacePath('build', 'device-p2a-browser')
mkdirSync(output, { recursive: true })
const gitSha = process.env.NETCONSOLE_P2A_GIT_SHA || execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo, encoding: 'utf8' }).trim()
const token = readFileSync(join(dataRoot, 'local-api-token'), 'utf8').trim()
const diagnostics = {
  gitSha,
  base,
  viewports: [{ width: 1920, height: 1080 }, { width: 1440, height: 900 }, { width: 1366, height: 768 }],
  consoleErrors: [], pageErrors: [], failedRequests: [], unexpectedHttpErrors: [], websocketErrors: [], unhandledRejections: [],
  expectedHttp: [], screenshots: [], passed: false,
}
const expected = []
const allowHttp = (method, pattern, status) => {
  expected.push({ method, pattern, status })
  diagnostics.expectedHttp.push({ method, pattern: String(pattern), status })
}
const expectedMatch = (request, response) => {
  const method = request.method()
  const url = new URL(response.url())
  const item = expected.find(candidate => candidate.method === method && candidate.status === response.status() && new RegExp(candidate.pattern).test(url.pathname))
  if (item) return true
  return false
}
const expectedConsoleError = text => {
  const match = text.match(/status of (\d{3}) \(/i)
  if (!match) return false
  const status = Number(match[1])
  return expected.some(candidate => candidate.status === status)
}
const expectedRequestFailure = request => {
  const url = new URL(request.url())
  return expected.some(candidate => candidate.method === request.method() && candidate.status === 403 && new RegExp(candidate.pattern).test(url.pathname))
}
const attachDiagnostics = (page, name) => {
  page.on('console', message => {
    if (message.type() === 'error' && !expectedConsoleError(message.text())) diagnostics.consoleErrors.push({ page: name, text: message.text() })
  })
  page.on('pageerror', error => diagnostics.pageErrors.push({ page: name, text: String(error) }))
  page.on('requestfailed', request => {
    if (!expectedRequestFailure(request)) diagnostics.failedRequests.push({ page: name, method: request.method(), url: request.url(), failure: request.failure()?.errorText || 'unknown' })
  })
  page.on('response', response => {
    const status = response.status()
    if (status >= 500 || (status >= 400 && !expectedMatch(response.request(), response)))
      diagnostics.unexpectedHttpErrors.push({ page: name, method: response.request().method(), status, url: response.url() })
  })
  page.on('websocket', socket => socket.on('socketerror', error => diagnostics.websocketErrors.push({ page: name, url: socket.url(), text: String(error) })))
  page.addInitScript(() => {
    window.__netconsoleUnhandledRejections = []
    window.addEventListener('unhandledrejection', event => window.__netconsoleUnhandledRejections.push(String(event.reason)))
  })
}

const directApi = async (path, method = 'GET', body = undefined) => {
  const response = await fetch(base + path, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  let payload = null
  try { payload = text ? JSON.parse(text) : null } catch { payload = text }
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status}: ${text}`)
  return payload
}

const seed = async () => {
  const devices = []
  for (let index = 1; index <= 6; index += 1) {
    const detail = await directApi('/api/system/devices', 'POST', {
      id: `browser-p2a-${index}`, name: `Browser P2A ${index}`, managementIp: `192.0.2.${110 + index}`,
      vendor: 'H3C', model: 'S5720', deviceType: 'AP', siteId: 'local', description: 'isolated browser fixture', enabled: true,
    })
    devices.push(detail.item)
  }
  await directApi('/api/system/device-groups', 'POST', { siteId: 'local', name: 'Browser P2A Group', description: 'isolated browser fixture' })
  return devices
}

const login = async (page, username, password) => {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' })
  await page.locator('input[autocomplete="username"]').fill(username)
  await page.locator('input[autocomplete="current-password"]').fill(password)
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
}
const gotoDevices = async page => {
  await page.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.locator('tbody tr').filter({ hasText: 'Browser P2A' }).first().waitFor()
}
const selectRows = async (page, indexes) => {
  for (const index of indexes) {
    const row = page.locator('tbody tr').filter({ hasText: `Browser P2A ${index}` }).first()
    await row.locator('input[type="checkbox"]').check()
  }
}
const closeDialog = async (page, name) => {
  const dialog = page.getByRole('dialog', { name }).last()
  if (await dialog.count() && await dialog.isVisible()) {
    const close = dialog.getByRole('button', { name: '关闭', exact: true })
    if (await close.count()) {
      await close.click()
      await dialog.waitFor({ state: 'hidden' })
    }
  }
}
const createPreview = async (page, buttonLabel, dialogLabel = buttonLabel, configure = undefined) => {
  await page.getByRole('button', { name: buttonLabel, exact: true }).click()
  const input = page.getByRole('dialog', { name: dialogLabel })
  if (configure) await configure(input)
  try {
    await input.getByRole('button', { name: '生成预览' }).click()
  } catch (error) {
    throw error
  }
  const preview = page.getByRole('dialog', { name: '批量操作预览' })
  await preview.getByText('待确认').waitFor()
  await page.waitForTimeout(400)
  return preview
}
const confirmPreview = async (page, preview, label = '确认批量提交') => {
  await preview.getByRole('button', { name: label }).click()
  await page.getByText('批量操作已原子提交', { exact: true }).last().waitFor()
}
const screenshot = async (page, name) => {
  const path = join(output, name)
  await page.screenshot({ path, fullPage: true })
  diagnostics.screenshots.push({ name, path, width: page.viewportSize().width, height: page.viewportSize().height })
}
const assertInViewport = async (page, locator, label, allowVerticalOverflow = false) => {
  const box = await locator.boundingBox()
  const viewport = page.viewportSize()
  if (!box || box.x < 0 || box.y < 0 || box.x + box.width > viewport.width || (!allowVerticalOverflow && box.y + box.height > viewport.height))
    throw new Error(`${label} is outside viewport ${viewport.width}x${viewport.height}: ${JSON.stringify(box)}`)
}
const updateDeviceDirectly = async id => {
  const detail = await directApi(`/api/system/devices/${encodeURIComponent(id)}`)
  const item = detail.item
  await directApi(`/api/system/devices/${encodeURIComponent(id)}`, 'PUT', {
    id: item.id, name: item.name, managementIp: item.managementIp || item.managementAddress,
    vendor: item.vendor, model: item.model, deviceType: item.deviceType, siteId: item.siteId || 'local',
    groupId: item.groupId || null, description: `${detail.description} conflict`, enabled: item.enabled,
    terminalProfileId: detail.terminalProfileId || null,
  })
}
const markFailedFixture = async operationId => {
  const script = "import sqlite3,sys; db=sqlite3.connect(sys.argv[1]); db.execute(\"UPDATE device_operations SET state='FAILED',completed_ms=NULL WHERE id=?\",(sys.argv[2],)); db.execute(\"UPDATE device_operation_items SET outcome='FAILED',detail='INJECTED_BROWSER_FAILURE' WHERE operation_id=?\",(sys.argv[2],)); db.commit(); db.close()"
  execFileSync('python', ['-c', script, join(dataRoot, 'catalog.db'), operationId], { stdio: 'pipe' })
}

const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', args: ['--no-sandbox'] })
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
const page = await context.newPage()
attachDiagnostics(page, 'superadmin')
allowHttp('GET', '^/api/auth/me$', 401)
allowHttp('GET', '^/api/system/terminal/devices$', 404)
let adminContext
let admin
try {
  await page.addInitScript(() => localStorage.setItem('netconsole-theme', 'light'))
  await seed()
  await login(page, 'superadmin', 'superadmin')
  await screenshot(page, 'overview-light.png')
  await page.getByTitle('切换深色主题').click()
  await page.locator('html[data-theme="dark"]').waitFor()
  await page.setViewportSize({ width: 1440, height: 900 })
  await screenshot(page, 'overview-dark.png')

  await page.setViewportSize({ width: 1366, height: 768 })
  await gotoDevices(page)
  await assertInViewport(page, page.getByRole('button', { name: '批量启用', exact: true }), 'batch toolbar')

  await selectRows(page, [1, 2])
  let preview = await createPreview(page, '批量禁用')
  await assertInViewport(page, preview.locator('.el-dialog'), 'batch preview dialog', true)
  await screenshot(page, 'batch-preview.png')
  await page.reload({ waitUntil: 'networkidle' })
  preview = page.getByRole('dialog', { name: '批量操作预览' })
  await preview.getByText('待确认').waitFor()
  await screenshot(page, 'batch-recovery.png')
  await confirmPreview(page, preview)
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [1])
  preview = await createPreview(page, '批量启用')
  await confirmPreview(page, preview)
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [1, 2])
  preview = await createPreview(page, '设置分组', '批量设置分组', async input => {
    await input.locator('label.form-field').filter({ hasText: '目标分组' }).locator('select').selectOption({ label: 'Browser P2A Group · local' })
  })
  await confirmPreview(page, preview)
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [1])
  preview = await createPreview(page, '设置局点', '批量设置局点', async input => {
    await input.locator('label.form-field').filter({ hasText: '目标局点' }).locator('input').fill('local')
  })
  await confirmPreview(page, preview)
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [1])
  preview = await createPreview(page, '更新元数据', '批量更新元数据', async input => {
    await input.locator('label.form-field').filter({ hasText: '名称（留空保持）' }).locator('input').fill('Browser P2A Metadata')
  })
  await confirmPreview(page, preview)
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [3])
  preview = await createPreview(page, '批量禁用')
  await preview.getByRole('button', { name: '取消操作' }).click()
  await preview.getByText('已取消').waitFor()
  await screenshot(page, 'batch-cancel.png')
  await closeDialog(page, '批量操作预览')

  await selectRows(page, [4])
  preview = await createPreview(page, '批量启用')
  const conflictConfirm = preview.getByRole('button', { name: '确认批量提交' })
  await updateDeviceDirectly('browser-p2a-4')
  allowHttp('POST', '^/api/system/devices/batch/.+/confirm$', 409)
  await conflictConfirm.click()
  await preview.getByText('发生冲突').waitFor()
  await screenshot(page, 'batch-conflict.png')
  await closeDialog(page, '批量操作预览')

  await page.getByRole('button', { name: '清除选择' }).click().catch(() => {})
  await selectRows(page, [5])
  preview = await createPreview(page, '批量启用')
  const operationId = await preview.locator('.mono').first().textContent()
  await markFailedFixture(operationId.trim())
  await preview.getByRole('button', { name: '重新加载状态' }).click()
  await preview.getByText('失败可重试').waitFor()
  await preview.getByRole('button', { name: '安全重试' }).click()
  await page.getByText('批量操作已原子提交', { exact: true }).last().waitFor()
  await closeDialog(page, '批量操作预览')

  adminContext = await browser.newContext({ viewport: { width: 1366, height: 768 } })
  admin = await adminContext.newPage()
  attachDiagnostics(admin, 'admin')
  allowHttp('GET', '^/api/auth/me$', 401)
  allowHttp('GET', '^/api/system/terminal/devices$', 404)
  await login(admin, 'admin', 'admin')
  await admin.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  if (await admin.getByRole('button', { name: '批量启用', exact: true }).count()) throw new Error('ADMIN must not see batch write controls')
  allowHttp('POST', '^/api/system/devices$', 403)
  const forbidden = await admin.evaluate(async () => (await fetch('/api/system/devices', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: 'denied', managementIp: '192.0.2.199', siteId: 'local', enabled: true }) })).status)
  if (forbidden !== 403) throw new Error(`ADMIN write expected 403, got ${forbidden}`)
  await admin.getByRole('button', { name: '退出' }).click()
  await admin.getByRole('button', { name: '登录工作台' }).waitFor()
  await adminContext.close()
  adminContext = null

  await page.getByRole('button', { name: '退出' }).click()
  await page.getByRole('button', { name: '登录工作台' }).waitFor()
  await login(page, 'superadmin', 'superadmin')
  for (const viewport of diagnostics.viewports) {
    await page.setViewportSize(viewport)
    await gotoDevices(page)
    await assertInViewport(page, page.getByRole('heading', { name: '设备管理' }), `device heading ${viewport.width}x${viewport.height}`)
  }

  diagnostics.unhandledRejections = await page.evaluate(() => window.__netconsoleUnhandledRejections || [])
  diagnostics.passed = true
  console.log('PASS: P2-A browser diagnostics, light/dark themes, five batch kinds, preview/confirm/cancel/conflict/retry/recovery and admin isolation')
} finally {
  if (adminContext) await adminContext.close()
  try { diagnostics.unhandledRejections = await page.evaluate(() => window.__netconsoleUnhandledRejections || []) } catch {}
  writeFileSync(join(output, 'browser-evidence.json'), JSON.stringify(diagnostics, null, 2), 'utf8')
  await browser.close()
}
if (!diagnostics.passed) process.exitCode = 1
