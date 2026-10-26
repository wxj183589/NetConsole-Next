import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { chromium } from '../web/node_modules/playwright/index.mjs'

const base = process.env.NETCONSOLE_E2E_BASE_URL || 'http://127.0.0.1:18773'
const output = fileURLToPath(new URL('../artifacts/device-management-browser/', import.meta.url))
mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', args: ['--no-sandbox'] })
const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 })
const page = await context.newPage()

async function login(target, username, password) {
  await target.goto(`${base}/login`, { waitUntil: 'networkidle' })
  await target.locator('input[autocomplete="username"]').fill(username)
  await target.locator('input[autocomplete="current-password"]').fill(password)
  await target.getByRole('button', { name: '登录工作台' }).click()
  await target.getByRole('heading', { name: '运行总览' }).waitFor()
}

try {
  await login(page, 'superadmin', 'superadmin')
  await page.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.getByRole('button', { name: '新增设备' }).click()
  const dialog = page.getByRole('dialog', { name: '新增设备' })
  await dialog.locator('label.form-field').filter({ hasText: '设备名称' }).locator('input').fill('Browser Phase1 AP')
  await dialog.locator('label.form-field').filter({ hasText: '管理 IP' }).locator('input').fill('192.0.2.90')
  await dialog.locator('label.form-field').filter({ hasText: '厂商' }).locator('input').fill('H3C')
  await dialog.locator('label.form-field').filter({ hasText: '型号' }).locator('input').fill('S5720')
  await dialog.getByRole('button', { name: '保存' }).click()
  await page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).waitFor()
  await page.screenshot({ path: `${output}/device-created.png`, fullPage: true })

  const row = page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).first()
  await row.getByRole('button', { name: '编辑' }).click()
  const edit = page.getByRole('dialog', { name: '编辑设备' })
  await edit.locator('label.form-field').filter({ hasText: '管理 IP' }).locator('input').fill('192.0.2.91')
  await edit.getByRole('button', { name: '保存' }).click()
  await page.getByText('192.0.2.91').waitFor()
  await page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).first().getByRole('button', { name: '详情' }).click()
  const details = page.getByRole('dialog', { name: '设备详情 / 审计' })
  await details.getByText('DEVICE_CREATE').waitFor()
  await page.screenshot({ path: `${output}/device-detail-audit.png`, fullPage: true })
  await page.keyboard.press('Escape')
  await details.waitFor({ state: 'hidden' })

  await page.getByRole('button', { name: '分组管理' }).click()
  const groupDialog = page.getByRole('dialog', { name: '设备分组管理' })
  await groupDialog.locator('label.form-field').filter({ hasText: '分组名称' }).locator('input').fill('Browser Group')
  await groupDialog.getByRole('button', { name: '新增分组' }).click()
  await groupDialog.getByText('Browser Group · 0 台').waitFor()
  await page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).first().getByRole('button', { name: '编辑' }).click()
  const groupEdit = page.getByRole('dialog', { name: '编辑设备' })
  await groupEdit.locator('label.form-field').filter({ hasText: '分组' }).locator('select').selectOption({ label: 'Browser Group' })
  await groupEdit.getByRole('button', { name: '保存' }).click()
  await page.locator('select[aria-label="设备分组"]').selectOption({ label: 'Browser Group' })
  await page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).waitFor()

  const batchRow = page.locator('tbody tr').filter({ hasText: 'Browser Phase1 AP' }).first()
  await batchRow.locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: '批量禁用' }).click()
  const batchInput = page.getByRole('dialog', { name: '批量禁用' })
  await batchInput.getByRole('button', { name: '生成预览' }).click()
  const batchPreview = page.getByRole('dialog', { name: '批量操作预览' })
  await batchPreview.getByText('待确认').waitFor()
  await page.reload({ waitUntil: 'networkidle' })
  const recoveredBatchPreview = page.getByRole('dialog', { name: '批量操作预览' })
  await recoveredBatchPreview.getByText('待确认').waitFor()
  await recoveredBatchPreview.getByRole('button', { name: '确认批量提交' }).click()
  await page.getByText('批量操作已原子提交').waitFor()
  await page.screenshot({ path: `${output}/device-batch-recovered-and-committed.png`, fullPage: true })

  const csv = 'deviceId,name,managementIp,vendor,model,deviceType,siteId,groupName,description,enabled\n,browser-import,192.0.2.92,H3C,X,AP,local,Browser Group,imported,true'
  await page.locator('#device-import-file').setInputFiles({ name: 'devices.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) })
  const importDialog = page.getByRole('dialog', { name: 'CSV 导入预览' })
  await importDialog.getByText('browser-import').waitFor()
  await importDialog.getByRole('button', { name: '确认原子导入' }).click()
  await page.locator('tbody tr').filter({ hasText: 'browser-import' }).waitFor()
  await page.screenshot({ path: `${output}/device-imported.png`, fullPage: true })

  const conflictCsv = 'name,managementIp\nconflict-a,192.0.2.93\nconflict-b,192.0.2.93'
  await page.locator('#device-import-file').setInputFiles({ name: 'conflict.csv', mimeType: 'text/csv', buffer: Buffer.from(conflictCsv) })
  const conflictDialog = page.getByRole('dialog', { name: 'CSV 导入预览' })
  await conflictDialog.getByText('DUPLICATE_IMPORT_ADDRESS').waitFor()
  if (await conflictDialog.getByRole('button', { name: '确认原子导入' }).isEnabled()) throw new Error('conflicted CSV must not be committable')
  await conflictDialog.getByRole('button', { name: '取消' }).click()

  const adminContext = await browser.newContext({ viewport: { width: 1200, height: 800 } })
  const admin = await adminContext.newPage()
  await login(admin, 'admin', 'admin')
  const forbidden = await admin.evaluate(async () => {
    const response = await fetch('/api/system/devices', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: 'must-not-write', managementIp: '192.0.2.99', siteId: 'local', enabled: true }) })
    return response.status
  })
  if (forbidden !== 403) throw new Error(`ADMIN write should be forbidden, got ${forbidden}`)
  await adminContext.close()
  console.log('PASS: browser CRUD, stable IP edit, detail/audit, group assignment, CSV preview/commit/conflict and ADMIN write denial')
} finally {
  await browser.close()
}
