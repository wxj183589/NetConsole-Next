import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.NETCONSOLE_E2E_BASE_URL || 'http://127.0.0.1:18883'
const dataRoot = process.env.NETCONSOLE_E2E_DATA_ROOT
if (!dataRoot) throw new Error('NETCONSOLE_E2E_DATA_ROOT must point to the isolated browser data root')
const output = workspacePath('build', process.env.NETCONSOLE_DEVICE_WORKSPACE_OUTPUT || 'device-management-workspace-browser')
mkdirSync(output, { recursive: true })
const token = readFileSync(join(dataRoot, 'local-api-token'), 'utf8').trim()
const evidence = {
  base, dataRoot, passed: false, screenshots: [], checks: [], duplicateAddressStatus: null,
  consoleErrors: [], pageErrors: [], failedRequests: [], unexpectedHttpErrors: [],
}

async function rawApi(path, method = 'GET', body = undefined) {
  const response = await fetch(base + path, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'content-type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  let payload = null
  try { payload = text ? JSON.parse(text) : null } catch { payload = text }
  return { status: response.status, payload, text }
}

async function directApi(path, method = 'GET', body = undefined) {
  const result = await rawApi(path, method, body)
  if (result.status < 200 || result.status >= 300) throw new Error(`${method} ${path} -> ${result.status}: ${result.text}`)
  return result.payload
}

async function ensureSeed() {
  const sites = await directApi('/api/system/sites')
  let siteB = sites.find(site => site.id === 'browser-site-b')
  if (!siteB) siteB = await directApi('/api/system/sites', 'POST', { id: 'browser-site-b', name: '浏览器验收 Site B', description: 'isolated device workspace fixture' })
  const groups = await directApi('/api/system/device-groups')
  let localGroup = groups.find(group => group.siteId === 'local' && group.name === '验收交换机组')
  if (!localGroup) localGroup = await directApi('/api/system/device-groups', 'POST', { siteId: 'local', name: '验收交换机组', description: 'isolated device workspace fixture' })
  let siteBGroup = groups.find(group => group.siteId === siteB.id && group.name === '验收无线组')
  if (!siteBGroup) siteBGroup = await directApi('/api/system/device-groups', 'POST', { siteId: siteB.id, name: '验收无线组', description: 'isolated device workspace fixture' })

  const current = await directApi('/api/system/devices?page=1&pageSize=100')
  const existing = new Set(current.items.map(item => item.id))
  const fixtures = []
  for (let index = 1; index <= 24; index += 1) {
    const isLocal = index <= 12
    const siteId = isLocal ? 'local' : siteB.id
    const serial = String(index).padStart(2, '0')
    const type = isLocal
      ? (index === 2 ? 'H3C_ROUTER' : 'H3C_SWITCH')
      : (index === 14 ? 'H3C_ROUTER' : index === 15 ? 'H3C_SWITCH' : 'NETWORK_DEVICE')
    const vendor = isLocal || index <= 15 ? 'H3C' : 'ZTE'
    const groupId = isLocal ? (index === 2 ? null : localGroup.id) : (index === 14 ? null : siteBGroup.id)
    const device = {
      id: `workspace-${isLocal ? 'local' : 'siteb'}-${serial}`,
      name: `工作台 ${isLocal ? 'Local' : 'Site B'} ${serial}`,
      managementIp: `198.51.100.${index}`,
      vendor, model: vendor === 'H3C' ? (type.includes('WIRELESS') ? 'WX-TEST' : 'S5130') : 'ZXR-TEST',
      deviceType: type, siteId, groupId, description: 'isolated browser workspace fixture', enabled: true,
    }
    fixtures.push(device)
    if (!existing.has(device.id)) await directApi('/api/system/devices', 'POST', device)
  }
  const duplicate = await rawApi('/api/system/devices', 'POST', { ...fixtures[0], id: 'workspace-duplicate-address', name: '重复地址拒绝验证' })
  evidence.duplicateAddressStatus = duplicate.status
  if (duplicate.status !== 409) throw new Error(`duplicate management IP should be rejected, got ${duplicate.status}`)
  evidence.checks.push('seeded two Sites, two groups, 24 isolated devices and duplicate-address rejection')
  return { siteB, localGroup, siteBGroup, fixtures }
}

function attachDiagnostics(page, name) {
  page.on('console', message => {
    if (message.type() !== 'error') return
    const match = message.text().match(/status of (\d{3})/i)
    if (match && [401, 403, 404].includes(Number(match[1]))) return
    evidence.consoleErrors.push({ page: name, text: message.text() })
  })
  page.on('pageerror', error => evidence.pageErrors.push({ page: name, text: String(error) }))
  page.on('requestfailed', request => {
    const url = new URL(request.url())
    if (name === 'admin' && request.method() === 'POST' && url.pathname === '/api/system/devices') return
    evidence.failedRequests.push({ page: name, method: request.method(), url: request.url(), failure: request.failure()?.errorText || 'unknown' })
  })
  page.on('response', response => {
    const status = response.status()
    if (status >= 500 || (status >= 400 && !(status === 401 || status === 404 || status === 403))) evidence.unexpectedHttpErrors.push({ page: name, method: response.request().method(), status, url: response.url() })
  })
}

async function login(page, username, password) {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' })
  await page.locator('input[autocomplete="username"]').fill(username)
  await page.locator('input[autocomplete="current-password"]').fill(password)
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
}

async function gotoDevices(page) {
  await page.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.locator('.device-tree-panel').waitFor()
  await page.waitForTimeout(250)
}

async function hasRow(page, text) {
  return await page.locator('tbody tr').filter({ hasText: text }).count() > 0
}

async function waitForRow(page, text, present = true) {
  await page.waitForFunction(({ text, present }) => {
    const found = [...document.querySelectorAll('tbody tr')].some(row => row.textContent?.includes(text))
    return found === present
  }, { text, present })
}

async function screenshot(page, name) {
  const path = join(output, name)
  await page.screenshot({ path, fullPage: true })
  evidence.screenshots.push({ name, path, viewport: page.viewportSize() })
}

async function selectAllSites(page) {
  await page.locator('.device-tree-node').filter({ hasText: '全部授权 Site' }).first().click()
  await page.waitForFunction(() => document.querySelector('.workspace-heading h2')?.textContent?.includes('全部授权 Site'))
}

function check(condition, message) {
  if (!condition) throw new Error(message)
  evidence.checks.push(message)
}

const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', args: ['--no-sandbox'] })
const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, deviceScaleFactor: 1 })
const page = await context.newPage()
attachDiagnostics(page, 'superadmin')
let adminContext
let densityContext
try {
  const fixture = await ensureSeed()
  await login(page, 'superadmin', 'superadmin')
  await gotoDevices(page)
  check(await page.getByText('全部授权 Site', { exact: false }).count() > 0, 'organization tree shows All authorized Sites')
  check(await page.locator('.device-tree-node').filter({ hasText: '未分组' }).count() > 0, 'organization tree shows ungrouped devices')
  check(await page.locator('select[aria-label="设备厂商"]').count() === 1, 'vendor filter is rendered')
  check(await page.locator('select[aria-label="设备类型"]').count() === 1, 'device type filter is rendered')
  await waitForRow(page, '工作台 Local 01')
  await screenshot(page, 'device-workspace-1366x768.png')

  await page.setViewportSize({ width: 1920, height: 1080 })
  await gotoDevices(page)
  await screenshot(page, 'device-workspace-1920x1080.png')
  await page.setViewportSize({ width: 2560, height: 1440 })
  await gotoDevices(page)
  await screenshot(page, 'device-workspace-2560x1440.png')

  await selectAllSites(page)
  await waitForRow(page, '工作台 Site B 13')
  check(await hasRow(page, '工作台 Local 12'), 'All Sites view includes Local inventory')
  check(await hasRow(page, '工作台 Site B 13'), 'All Sites view includes Site B inventory')

  await page.locator('.device-tree-node').filter({ hasText: '浏览器验收 Site B' }).first().click()
  await waitForRow(page, '工作台 Site B 13')
  await waitForRow(page, '工作台 Local 01', false)
  check(true, 'Site B tree selection applies server-side Site filter')

  await page.locator('.device-tree-node').filter({ hasText: fixture.siteBGroup.name }).click()
  await waitForRow(page, '工作台 Site B 13')
  await waitForRow(page, '工作台 Site B 14', false)
  check(true, 'group tree selection applies group filter within Site')

  await page.locator('.device-tree-node').filter({ hasText: '浏览器验收 Site B' }).first().click()
  await page.locator('select[aria-label="设备厂商"]').selectOption('H3C')
  await waitForRow(page, '工作台 Site B 13')
  await waitForRow(page, '工作台 Site B 16', false)
  check(true, 'vendor filter excludes ZTE rows through server query')
  const typeResponse = page.waitForResponse(response => {
    const url = new URL(response.url())
    return response.request().method() === 'GET' && url.pathname === '/api/system/devices' && url.searchParams.get('deviceType') === 'H3C_ROUTER'
  })
  await page.locator('select[aria-label="设备类型"]').selectOption('H3C_ROUTER')
  await typeResponse
  await waitForRow(page, '工作台 Site B 14')
  const typeRowCount = await page.locator('tbody tr').filter({ hasText: '工作台 Site B' }).count()
  check(typeRowCount === 1, `device type filter narrows the result (rows=${typeRowCount})`)
  await page.getByTitle('清除筛选').click()
  await waitForRow(page, '工作台 Site B 13')
  const searchResponse = page.waitForResponse(response => {
    const url = new URL(response.url())
    return response.request().method() === 'GET' && url.pathname === '/api/system/devices' && url.searchParams.get('search') === '工作台 Site B 15'
  })
  await page.locator('label.device-filter-search input').fill('工作台 Site B 15')
  await page.locator('label.device-filter-search input').press('Enter')
  await searchResponse
  await waitForRow(page, '工作台 Site B 15')
  const searchRowCount = await page.locator('tbody tr').count()
  check(searchRowCount === 1, `search covers device identity and metadata (rows=${searchRowCount})`)
  await page.getByTitle('清除筛选').click()

  await selectAllSites(page)
  await waitForRow(page, '工作台 Local 12')
  await page.locator('.device-tree-node').filter({ hasText: fixture.siteBGroup.name }).waitFor()
  await page.locator('select[aria-label="每页"]')
    .selectOption('20').catch(() => {})
  await page.locator('tbody tr').filter({ hasText: '工作台 Local 12' }).first().locator('input[type="checkbox"]').check()
  await page.locator('tbody tr').filter({ hasText: '工作台 Local 12' }).first().getByRole('button', { name: '详情' }).click()
  const details = page.getByRole('dialog', { name: '设备详情 / 审计' })
  await details.waitFor()
  check((await details.textContent()).includes('工作台 Local 12'), 'device detail opens from the main workspace')
  await screenshot(page, 'device-workspace-detail.png')
  await page.keyboard.press('Escape')

  await page.locator('tbody tr').filter({ hasText: '工作台 Local 12' }).first().getByRole('button', { name: '编辑' }).click()
  await page.waitForURL(/\/devices\/[^/]+\/edit$/)
  check(page.url().includes('/edit'), 'edit action opens the existing device edit flow')
  await gotoDevices(page)
  await selectAllSites(page)
  await waitForRow(page, '工作台 Local 12')
  await page.locator('.device-tree-node').filter({ hasText: fixture.siteBGroup.name }).waitFor()

  await page.locator('tbody tr').filter({ hasText: '工作台 Local 12' }).first().locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: '设置分组', exact: true }).click()
  const batchDialog = page.getByRole('dialog', { name: '批量设置分组' })
  await batchDialog.locator('select').selectOption({ label: `${fixture.siteBGroup.name} · ${fixture.siteB.id}` })
  await batchDialog.getByRole('button', { name: '生成预览' }).click()
  const conflict = page.getByRole('dialog', { name: '批量操作预览' })
  await conflict.getByText('发生冲突').waitFor()
  check(await conflict.getByText('GROUP_SITE_MISMATCH').count() > 0, 'cross-Site group operation is rejected by the service')
  await screenshot(page, 'device-workspace-cross-site-conflict.png')
  await conflict.getByRole('button', { name: '关闭' }).click()

  await page.getByRole('button', { name: '清除选择', exact: true }).click()
  await page.locator('label.device-filter-search input').fill('不存在的设备-验收')
  await page.locator('label.device-filter-search input').press('Enter')
  await page.getByText('没有符合条件的设备', { exact: true }).waitFor()
  await screenshot(page, 'device-workspace-empty-state.png')

  const sideLeft = await browser.newContext({ viewport: { width: 960, height: 900 }, deviceScaleFactor: 1 })
  const sideRight = await browser.newContext({ viewport: { width: 960, height: 900 }, deviceScaleFactor: 1 })
  const leftPage = await sideLeft.newPage(); const rightPage = await sideRight.newPage()
  attachDiagnostics(leftPage, 'side-left'); attachDiagnostics(rightPage, 'side-right')
  await Promise.all([login(leftPage, 'superadmin', 'superadmin'), login(rightPage, 'superadmin', 'superadmin')])
  await Promise.all([gotoDevices(leftPage), gotoDevices(rightPage)])
  await screenshot(leftPage, 'device-workspace-side-by-side-left.png')
  await screenshot(rightPage, 'device-workspace-side-by-side-right.png')
  await sideLeft.close(); await sideRight.close()
  evidence.checks.push('two independent browser windows render the workspace concurrently')

  densityContext = await browser.newContext({ viewport: { width: 1536, height: 864 }, deviceScaleFactor: 1.25 })
  const densityPage = await densityContext.newPage()
  attachDiagnostics(densityPage, 'windows-125-percent')
  await login(densityPage, 'superadmin', 'superadmin')
  await gotoDevices(densityPage)
  check(await densityPage.locator('.device-tree-panel').isVisible(), 'Windows 125% equivalent browser scale keeps organization tree visible')
  await screenshot(densityPage, 'device-workspace-windows-125-percent.png')
  await densityContext.close(); densityContext = null

  adminContext = await browser.newContext({ viewport: { width: 1366, height: 768 }, deviceScaleFactor: 1 })
  const admin = await adminContext.newPage()
  attachDiagnostics(admin, 'admin')
  await login(admin, 'admin', 'admin')
  await admin.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  check(await admin.getByRole('button', { name: '批量启用', exact: true }).count() === 0, 'ADMIN does not receive batch write controls')
  const forbidden = await admin.evaluate(async () => (await fetch('/api/system/devices', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id: 'admin-denied', name: 'denied', managementIp: '198.51.100.250', siteId: 'local', enabled: true }) })).status)
  check(forbidden === 403, `ADMIN direct device write denied with 403 (got ${forbidden})`)
  await adminContext.close(); adminContext = null

  evidence.passed = true
  console.log('PASS: device workspace tree, Site/group scope, vendor/type filters, cross-Site rejection, responsive screenshots and ADMIN write denial')
} finally {
  if (adminContext) await adminContext.close()
  if (densityContext) await densityContext.close()
  try { await context.close() } catch {}
  evidence.consoleErrors = evidence.consoleErrors.slice(0, 50)
  writeFileSync(join(output, 'browser-evidence.json'), JSON.stringify(evidence, null, 2), 'utf8')
  await browser.close()
}
if (!evidence.passed) process.exitCode = 1
