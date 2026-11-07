import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

// The caller owns the isolated service and its data root. API traffic is routed
// to it while Vite serves the current source on its separate port.
const ui = process.env.R68_UI_URL || 'http://127.0.0.1:5174'
const api = process.env.R68_API_URL || 'http://127.0.0.1:18772'
const output = workspacePath('reports', 'r68-browser')
mkdirSync(output, { recursive: true })
const result = { evidence: 'SIMULATOR', ui, api, checks: {}, apiFailures: [], pageErrors: [], consoleErrors: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
let authenticated = false
page.on('pageerror', error => result.pageErrors.push(error.message))
page.on('console', message => { if (message.type() === 'error' && (authenticated || !message.text().includes('401'))) result.consoleErrors.push(message.text()) })
page.on('response', response => {
  const pathname = new URL(response.url()).pathname
  if (pathname.startsWith('/api/') && response.status() >= 400 && !(pathname === '/api/auth/me' && !authenticated) && !(/^\/api\/system\/sites\/[^/]+\/ssh-jump-host$/.test(pathname) && response.status() === 404))
    result.apiFailures.push(`${response.status()} ${pathname}`)
})
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const url = new URL(route.request().url())
  const headers = { ...route.request().headers() }
  if (headers.origin) headers.origin = api
  await route.fulfill({ response: await route.fetch({ url: `${api}${url.pathname}${url.search}`, headers }) })
})
try {
  await page.goto(ui, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill(process.env.R68_USER || 'superadmin')
  await page.getByLabel('密码').fill(process.env.R68_PASSWORD || 'superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  authenticated = true
  result.checks.login = 'PASS'
  await page.locator('.app-sidebar').getByRole('button', { name: '设备管理' }).click()
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.getByText('192.168.56.2', { exact: true }).waitFor({ timeout: 15000 })
  const rows = page.locator('[data-testid="device-inventory-table"] tbody tr')
  result.checks.inventoryText = (await rows.allTextContents()).map(s => s.replace(/\s+/g, ' ').trim())
  const acRow = rows.filter({ hasText: '192.168.56.2' })
  await acRow.getByRole('button', { name: '详情' }).click()
  await page.getByRole('dialog', { name: '设备详情 / 审计' }).waitFor()
  result.checks.deviceDetail = 'PASS: AC1 detail opened'
  await page.getByRole('dialog', { name: '设备详情 / 审计' }).getByRole('button', { name: 'Close this dialog' }).click()
  await page.locator('.app-sidebar').getByRole('button', { name: '无线业务' }).click()
  await page.getByRole('heading', { name: '无线业务' }).waitFor()
  await page.getByRole('heading', { name: '无线任务历史' }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.screenshot({ path: path.join(output, `wireless-${width}.png`), fullPage: true })
  }
  const domain = page.locator('.wireless-task input[type=checkbox]').first()
  if (process.env.R68_READ_ONLY === '1') {
    result.checks.task = 'NOT_TESTED: read-only browser pass'
  } else if (!await domain.count()) {
    result.checks.task = 'BLOCKED: no visible AC Domain'
  } else {
    for (const checkbox of await page.locator('.wireless-task > div:nth-child(2) input[type=checkbox]').all()) await checkbox.check()
    await page.locator('.wireless-task').getByText('display wlan ap all', { exact: true }).locator('input').check()
    await page.getByRole('button', { name: '开始只读采集' }).click()
    await page.locator('.wireless-task-detail h3').waitFor({ timeout: 30000 })
    await page.waitForFunction(() => /任务详情 · (SUCCEEDED|PARTIAL|FAILED|CANCELLED|TIMED_OUT)/.test(document.querySelector('.wireless-task-detail h3')?.textContent || ''), null, { timeout: 180000 })
    result.checks.task = (await page.locator('.wireless-task-detail').innerText()).slice(0, 12000)
    const downloadPromise = page.waitForEvent('download')
    await page.getByRole('button', { name: '下载 ZIP' }).click()
    const download = await downloadPromise
    const archive = path.join(output, 'wireless-task.zip')
    await download.saveAs(archive)
    const script = 'import hashlib,json,sys,zipfile\nz=zipfile.ZipFile(sys.argv[1]); names=z.namelist(); m=json.loads(z.read("manifest.json")); checks=[{"name":f["name"],"ok":len(z.read(f["name"]))==f["bytes"] and hashlib.sha256(z.read(f["name"])).hexdigest()==f["sha256"]} for f in m["files"]]; print(json.dumps({"files":names,"manifest":m,"checks":checks},ensure_ascii=True))'
    result.checks.zip = JSON.parse(execFileSync('python', ['-c', script, archive], { encoding: 'utf8' }))
    if (!result.checks.zip.checks.every(item => item.ok)) throw new Error('ZIP manifest integrity check failed')
  }
  await page.locator('.app-sidebar').getByRole('button', { name: '配置备份' }).click()
  await page.getByRole('heading', { name: '配置备份', exact: true }).waitFor()
  await page.screenshot({ path: path.join(output, 'config-2560.png'), fullPage: true })
  result.checks.config = 'PARTIAL: page opened; backup and restore not exercised'
  result.overall = 'PARTIAL'
} catch (error) {
  result.overall = 'BLOCKED'
  result.error = String(error)
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
} finally {
  writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
  await browser.close()
}
console.log(JSON.stringify(result, null, 2))
if (result.error || result.apiFailures.length || result.pageErrors.length || result.consoleErrors.length) process.exitCode = 1
