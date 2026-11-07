import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const ui = 'http://127.0.0.1:5174'
const api = process.env.R69_API_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', 'r69-browser-20261107')
mkdirSync(output, { recursive: true })
const result = { evidence: 'SIMULATOR', ui, api, checks: {}, apiFailures: [], pageErrors: [], consoleErrors: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
let authenticated = false
page.on('pageerror', error => result.pageErrors.push(error.message))
page.on('console', message => { if (message.type() === 'error' && (authenticated || !message.text().includes('401'))) result.consoleErrors.push(message.text()) })
page.on('response', response => {
  const pathname = new URL(response.url()).pathname
  if (pathname.startsWith('/api/') && response.status() >= 400 && !(pathname === '/api/auth/me' && !authenticated) && !(/^\/api\/system\/sites\/[^/]+\/ssh-jump-host$/.test(pathname) && response.status() === 404)) result.apiFailures.push(`${response.status()} ${pathname}`)
})
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const requestUrl = new URL(route.request().url())
  const headers = { ...route.request().headers() }
  if (headers.origin) headers.origin = api
  await route.fulfill({ response: await route.fetch({ url: `${api}${requestUrl.pathname}${requestUrl.search}`, headers }) })
})
try {
  await page.goto(ui, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill(process.env.R69_USER || 'superadmin')
  await page.getByLabel('密码').fill(process.env.R69_PASSWORD || 'superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  authenticated = true
  result.checks.login = 'PASS'
  await page.screenshot({ path: path.join(output, 'dashboard-1920.png'), fullPage: true })

  await page.locator('.app-sidebar').getByRole('button', { name: '无线业务' }).click()
  await page.getByRole('heading', { name: '无线业务' }).waitFor()
  await page.getByRole('heading', { name: '无线任务历史' }).waitFor()
  await page.waitForTimeout(1500)
  result.checks.wirelessDomains = await page.locator('.wireless-tree__domain').count()
  result.checks.taskHistoryRows = await page.locator('.wireless-task-row').count()
  for (const viewport of [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
    await page.setViewportSize(viewport)
    await page.screenshot({ path: path.join(output, `wireless-${viewport.width}.png`), fullPage: true })
  }
  const taskButton = page.locator('.wireless-task-row button').first()
  if (await taskButton.count()) {
    await taskButton.click()
    await page.locator('.wireless-task-detail h3').waitFor({ timeout: 15000 })
    result.checks.taskDetail = (await page.locator('.wireless-task-detail').innerText()).slice(0, 12000)
    const downloadPromise = page.waitForEvent('download')
    await page.getByRole('button', { name: '下载 ZIP' }).click()
    const download = await downloadPromise
    const archive = path.join(output, 'existing-wireless-task.zip')
    await download.saveAs(archive)
    const script = 'import hashlib,json,sys,zipfile\nz=zipfile.ZipFile(sys.argv[1]); m=json.loads(z.read("manifest.json")); checks=[{"name":f["name"],"ok":len(z.read(f["name"]))==f["bytes"] and hashlib.sha256(z.read(f["name"])).hexdigest()==f["sha256"]} for f in m["files"]]; print(json.dumps({"names":z.namelist(),"manifest":m,"checks":checks},ensure_ascii=True))'
    result.checks.zip = JSON.parse(execFileSync('python', ['-c', script, archive], { encoding: 'utf8' }))
  } else result.checks.taskDetail = 'BLOCKED: no existing task history row'

  await page.locator('.app-sidebar').getByRole('button', { name: '配置备份' }).click()
  await page.getByRole('heading', { name: '配置备份', exact: true }).waitFor()
  await page.waitForTimeout(1000)
  result.checks.configText = (await page.locator('main').innerText()).slice(0, 10000)
  await page.screenshot({ path: path.join(output, 'config-1920.png'), fullPage: true })
  await page.setViewportSize({ width: 2560, height: 1440 })
  await page.screenshot({ path: path.join(output, 'config-2560.png'), fullPage: true })
  result.overall = result.checks.taskDetail?.startsWith('BLOCKED') ? 'PARTIAL' : 'PASS_WITH_LIMITATIONS'
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
