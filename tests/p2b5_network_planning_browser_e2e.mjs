import { mkdirSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { chromium } from '../web/node_modules/playwright/index.mjs'

const base = process.env.NETCONSOLE_E2E_BASE_URL || 'http://127.0.0.1:18775'
const output = process.env.NETCONSOLE_P2B5_ARTIFACT_DIR || fileURLToPath(new URL('../web/artifacts/p2b5-network-planning-browser/', import.meta.url))
mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', args: ['--no-sandbox'] })
const diagnostics = { consoleErrors: [], expectedConsoleErrors: [], pageErrors: [], failedRequests: [], expectedHttp: [], unexpectedHttp: [], screenshots: [], viewportResults: [] }
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
const page = await context.newPage()
page.on('console', (message) => { if (message.type() === 'error') (message.text().startsWith('Failed to load resource') ? diagnostics.expectedConsoleErrors : diagnostics.consoleErrors).push(message.text()) })
page.on('pageerror', (error) => diagnostics.pageErrors.push(String(error)))
page.on('requestfailed', (request) => diagnostics.failedRequests.push(`${request.method()} ${request.url()} · ${request.failure()?.errorText || 'failed'}`))
page.on('response', (response) => { if (response.status() >= 400) { const entry = `${response.status()} ${response.url()}`; if (response.status() === 401 && response.url().includes('/api/auth/me') || response.status() === 404 && response.url().endsWith('/favicon.ico')) diagnostics.expectedHttp.push(entry); else diagnostics.unexpectedHttp.push(entry) } })

async function login(target, username, password) {
  await target.goto(`${base}/login`, { waitUntil: 'networkidle' })
  await target.locator('input[autocomplete="username"]').fill(username)
  await target.locator('input[autocomplete="current-password"]').fill(password)
  await target.getByRole('button', { name: '登录工作台' }).click()
  await target.getByRole('heading', { name: '运行总览' }).waitFor()
}
async function screenshot(name, viewport) {
  await page.screenshot({ path: `${output}/${name}.png`, fullPage: true })
  diagnostics.screenshots.push({ name, viewport, path: `${output}/${name}.png` })
}
function field(dialog, label) { return dialog.getByText(label, { exact: true }).locator('..').locator('input,select,textarea').first() }

try {
  await login(page, 'superadmin', 'superadmin')
  await page.goto(`${base}/network-planning`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '网络规划管理' }).waitFor()
  await page.getByText('隔离写入已授权').waitFor()
  await page.locator('.planning-device-picker select').selectOption('p2b4-device-a')
  await page.getByText('10.0.0.30/24').first().waitFor()
  await screenshot('planning-light-1920x1080', '1920x1080-light')
  await page.setViewportSize({ width: 1680, height: 943 })
  await screenshot('planning-light-1680x943', '1680x943-light')
  await page.setViewportSize({ width: 1920, height: 1080 })

  await page.getByRole('button', { name: 'Network Domain' }).click()
  await page.getByText('P2B4 A Domain', { exact: true }).waitFor()
  await screenshot('domains-light-1920x1080', '1920x1080-light')

  await page.getByRole('button', { name: 'VLAN 规划' }).click()
  await page.getByText('实测').first().waitFor()
  await screenshot('vlans-light-1920x1080', '1920x1080-light')

  await page.getByRole('button', { name: '设备地址 / 成员' }).click()
  await page.getByRole('button', { name: '新增地址' }).click()
  const addressDialog = page.getByRole('dialog', { name: '新增地址事实' })
  await field(addressDialog, 'Network Domain').selectOption('p2b4-domain-a')
  await field(addressDialog, '地址').fill('10.0.0.50')
  await field(addressDialog, 'Source Reference').fill('p2b5-browser-address')
  await field(addressDialog, '接口').fill('eth-p2b5')
  await addressDialog.getByRole('button', { name: '保存' }).click()
  await page.getByText('地址操作已完成').waitFor()
  await page.getByText('10.0.0.50/24').waitFor()

  const createdAddressRow = page.locator('tbody tr').filter({ hasText: '10.0.0.50/24' }).first()
  await createdAddressRow.getByRole('button', { name: '编辑' }).click()
  const addressEditDialog = page.getByRole('dialog', { name: '编辑地址事实' })
  await field(addressEditDialog, '接口').fill('eth-p2b5-updated')
  await addressEditDialog.getByRole('button', { name: '保存' }).click()
  await page.getByText('地址操作已完成').last().waitFor()
  await page.locator('tbody tr').filter({ hasText: '10.0.0.50/24' }).first().getByRole('button', { name: '历史' }).click()
  await page.getByRole('dialog', { name: '地址 10.0.0.50/24 历史' }).getByText('Revision 1').waitFor()
  await page.keyboard.press('Escape')

  await page.getByRole('button', { name: 'VLAN 规划' }).click()
  await page.getByRole('button', { name: '新增 VLAN' }).click()
  const vlanDialog = page.getByRole('dialog', { name: '新增 VLAN 规划' })
  await field(vlanDialog, 'Network Domain').selectOption('p2b4-domain-a')
  await field(vlanDialog, 'VLAN ID').fill('321')
  await field(vlanDialog, '名称').fill('P2B5 Browser VLAN')
  await field(vlanDialog, '用途').fill('browser acceptance')
  await field(vlanDialog, 'PVID').fill('321')
  await field(vlanDialog, 'Source Reference').fill('p2b5-browser-vlan')
  await vlanDialog.getByRole('button', { name: '保存 VLAN 规划' }).click()
  await page.getByText('VLAN 操作已完成').waitFor()
  await page.getByText('321').first().waitFor()

  await page.getByRole('button', { name: '设备地址 / 成员' }).click()
  const addressRow = page.locator('tbody tr').filter({ hasText: '10.0.0.50/24' }).first()
  await addressRow.getByRole('button', { name: '审核' }).click()
  await page.getByRole('button', { name: '审核 / 导入' }).click()
  await field(page.locator('.planning-card').filter({ hasText: '人工审核' }).first(), '理由').fill('P2B5 browser review evidence')
  await page.getByRole('button', { name: '保存审核记录' }).click()
  await page.getByText('审核记录已保存').waitFor()

  const csv = 'entityType,deviceId,networkDomainId,addressFamily,normalizedAddress,prefixLength,addressRole,sourceKind,sourceReference\nADDRESS,p2b4-device-a,p2b4-domain-a,IPV4,10.0.0.51,24,SERVICE,IMPORTED,p2b5-browser-import'
  await page.getByRole('button', { name: '审核 / 导入' }).click()
  await field(page.locator('.planning-card').filter({ hasText: 'CSV 导入工作台' }).first(), 'CSV 内容').fill(csv)
  await field(page.locator('.planning-card').filter({ hasText: 'CSV 导入工作台' }).first(), '幂等键').fill('p2b5-browser-import-key')
  await page.getByRole('button', { name: '生成 Preview' }).click()
  await page.getByText('导入已完成校验').waitFor()
  await page.getByRole('button', { name: 'Dry-Run' }).click()
  await page.getByRole('button', { name: '明确 Confirm' }).click()
  await page.getByText('导入已在单事务中提交').waitFor()

  await page.getByRole('button', { name: '规划 vs 实测' }).click()
  await page.getByText('地址差异').waitFor()
  await screenshot('diff-light-1920x1080', '1920x1080-light')
  await page.getByRole('button', { name: '操作历史' }).click()
  await page.getByText('ADDRESS_CREATE').waitFor()
  await screenshot('operations-light-1920x1080', '1920x1080-light')

  await page.getByTitle('切换深色主题').click()
  await page.getByRole('button', { name: '设备地址 / 成员' }).click()
  await page.setViewportSize({ width: 1440, height: 900 })
  await screenshot('planning-dark-1440x900', '1440x900-dark')
  await page.setViewportSize({ width: 1366, height: 768 })
  await screenshot('planning-dark-1366x768', '1366x768-dark')
  diagnostics.viewportResults.push('1920x1080', '1440x900', '1366x768')

  const adminContext = await browser.newContext({ viewport: { width: 1440, height: 900 } })
  const admin = await adminContext.newPage()
  const adminErrors = []
  const adminExpectedHttp = []
  admin.on('console', (message) => { if (message.type() === 'error') (message.text().startsWith('Failed to load resource') ? adminExpectedHttp : adminErrors).push(message.text()) })
  admin.on('response', (response) => { if (response.status() >= 400) adminExpectedHttp.push(`${response.status()} ${response.url()}`) })
  await login(admin, 'admin', 'admin')
  await admin.goto(`${base}/network-planning`, { waitUntil: 'networkidle' })
  await admin.getByText('网络规划只读能力不可用').waitFor()
  if (await admin.getByRole('button', { name: '新增地址' }).count()) throw new Error('ADMIN must not see write controls when site scope is unavailable')
  await admin.screenshot({ path: `${output}/admin-no-site-scope.png`, fullPage: true })
  if (adminErrors.length) diagnostics.consoleErrors.push(...adminErrors.map((value) => `admin:${value}`))
  if (adminExpectedHttp.length) diagnostics.expectedHttp.push(...adminExpectedHttp.map((value) => `admin:${value}`))
  await adminContext.close()

  writeFileSync(`${output}/browser-evidence.json`, JSON.stringify({
    status: 'P2B5_BROWSER_E2E_PASS', base, screenshots: diagnostics.screenshots,
    viewportResults: diagnostics.viewportResults, diagnostics: {
      consoleErrorCount: diagnostics.consoleErrors.length, pageErrorCount: diagnostics.pageErrors.length,
      failedRequestCount: diagnostics.failedRequests.length, unexpectedHttpCount: diagnostics.unexpectedHttp.length,
      consoleErrors: diagnostics.consoleErrors, pageErrors: diagnostics.pageErrors,
      expectedConsoleErrors: diagnostics.expectedConsoleErrors, expectedHttp: diagnostics.expectedHttp,
      failedRequests: diagnostics.failedRequests, unexpectedHttp: diagnostics.unexpectedHttp,
    },
  }, null, 2))
  if (diagnostics.consoleErrors.length || diagnostics.pageErrors.length || diagnostics.failedRequests.length || diagnostics.unexpectedHttp.length) throw new Error(`browser diagnostics are not clean: ${JSON.stringify(diagnostics)}`)
  console.log('P2B5_BROWSER_E2E=PASS')
} finally {
  await browser.close()
}
