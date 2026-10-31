import { chromium } from '../web/node_modules/@playwright/test/index.mjs'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const baseUrl = process.env.NETCONSOLE_NEXT_UI_URL || 'http://127.0.0.1:18772'
const outputDir = workspacePath('build', process.env.NETCONSOLE_NEXT_UI_ARTIFACTS || 'ui-acceptance')
const chromePath = process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'
fs.mkdirSync(outputDir, { recursive: true })

const browser = await chromium.launch({ headless: true, executablePath: chromePath })
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
const page = await context.newPage()
const consoleErrors = []
const pageErrors = []
const failedRequests = []
const badResponses = []
const authOrMissingResponses = []
let authenticated = false
page.on('console', message => { if (authenticated && message.type() === 'error') consoleErrors.push(message.text()) })
page.on('pageerror', error => { if (authenticated) pageErrors.push(error.message) })
page.on('requestfailed', request => failedRequests.push(`${request.method()} ${request.url()} :: ${request.failure()?.errorText || 'failed'}`))
page.on('response', response => { if (response.status() >= 500) badResponses.push(`${response.status()} ${response.url()}`) })
page.on('response', response => { if ([401, 404].includes(response.status())) authOrMissingResponses.push(`${response.status()} ${response.url()}`) })

async function settle() {
  await page.waitForLoadState('domcontentloaded')
  await page.waitForTimeout(350)
}
async function screenshot(name) {
  await page.screenshot({ path: path.join(outputDir, name), fullPage: true })
}
async function visit(route, name) {
  await page.goto(`${baseUrl}${route}`, { waitUntil: 'domcontentloaded' })
  await settle()
  if (page.url().endsWith('/login')) throw new Error(`authentication redirect while opening ${route}`)
  await screenshot(name)
}

await page.goto(`${baseUrl}/login`, { waitUntil: 'domcontentloaded' })
await page.getByLabel('用户名').fill('admin')
await page.getByLabel('密码').fill('admin')
await page.getByRole('button', { name: /登录/ }).click()
await page.waitForURL(`${baseUrl}/`)
authenticated = true
authOrMissingResponses.length = 0
await settle()
await screenshot('overview-light.png')

await page.getByTitle('切换深色主题').click()
await settle()
await screenshot('overview-dark.png')
await page.getByTitle('切换浅色主题').click()

await visit('/devices', 'devices-light.png')
const deviceRows = await page.locator('.data-table tbody .device-name').count()
await page.getByTitle('切换深色主题').click()
await settle()
await screenshot('devices-dark.png')
await page.getByTitle('切换浅色主题').click()

await visit('/terminal', 'terminal-dark.png')
await visit('/tasks', 'tasks.png')
await visit('/tools', 'tools.png')
await visit('/files', 'files.png')
await visit('/settings', 'settings.png')

for (const viewport of [{ width: 1440, height: 900 }, { width: 1366, height: 768 }]) {
  await page.setViewportSize(viewport)
  await page.goto(`${baseUrl}/`, { waitUntil: 'domcontentloaded' })
  await settle()
  await screenshot(`overview-${viewport.width}x${viewport.height}.png`)
}

const report = {
  baseUrl,
  chromePath,
  viewportChecks: ['1920x1080', '1440x900', '1366x768'],
  screenshots: fs.readdirSync(outputDir).filter(file => file.endsWith('.png')).sort(),
  consoleErrors,
  pageErrors,
  failedRequests,
  badResponses,
  authOrMissingResponses,
  deviceRows,
}
fs.writeFileSync(path.join(outputDir, 'browser-evidence.json'), JSON.stringify(report, null, 2), 'utf8')
console.log(JSON.stringify(report, null, 2))
await browser.close()
if (consoleErrors.length || pageErrors.length || failedRequests.length || badResponses.length) process.exitCode = 1
