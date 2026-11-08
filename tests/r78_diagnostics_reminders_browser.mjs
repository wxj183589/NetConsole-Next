// UI at the reported base URL: actual backup reads, explicit ICMP/TCP fixtures.
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R78_UI_URL || 'http://127.0.0.1:5174'
const output = workspacePath('reports', `r78-diagnostics-reminders-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const result = { base, evidenceLevels: ['BROWSER_UI_ACTUAL_BACKUP_READS', 'BROWSER_UI_EXPLICIT_ICMP_TCP_FIXTURES'], checks: [], layouts: [], errors: [], deviceMutations: [], submissions: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const check = (value, label) => { if (!value) throw new Error(label); result.checks.push(label); save(); console.log(label + '=PASS') }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
let page
try {
  const context = await browser.newContext()
  await context.route('**/api/**', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname
    if (['GET', 'HEAD', 'OPTIONS'].includes(request.method()) || pathname === '/api/auth/login') return route.continue()
    result.deviceMutations.push({ pathname, method: request.method() }); await route.abort('blockedbyclient')
  })
  page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => result.errors.push(error.message))
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    const responsePending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/config-backups' && response.request().method() === 'GET')
    await page.goto(base + '/config-backups', { waitUntil: 'networkidle' })
    const response = await responsePending, history = await response.json()
    const reminder = page.getByRole('status', { name: '备份提醒', exact: true })
    await reminder.waitFor()
    check(response.status() === 200 && Array.isArray(history), `ACTUAL_BACKUP_API_${width}`)
    const latest = [...history].sort((a, b) => b.version - a.version)[0]
    const expected = !latest ? '尚无备份' : (latest.state && latest.state !== 'SUCCEEDED') || !Number.isSafeInteger(latest.capturedMs) || latest.capturedMs <= 0 || latest.capturedMs > Date.now()
      ? '时间待核验' : Date.now() - latest.capturedMs >= 7 * 86400000 ? '建议更新备份' : '已有近期备份'
    check((await reminder.innerText()).includes(expected), `REMINDER_MATCHES_ACTUAL_HISTORY_${width}`)
    check(await reminder.getByRole('button', { name: '备份当前设备', exact: true }).isEnabled(), `EXPLICIT_BACKUP_ACTION_${width}`)
    await page.screenshot({ path: path.join(output, `backup-reminder-${width}.png`), fullPage: true })
    const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ view: 'actual-backup-reminder', width, scrollWidth, rows: history.length, expected })
    check(scrollWidth <= width + 1, `BACKUP_NO_OVERFLOW_${width}`)
  }
  const response = await context.request.get(base + '/api/system/devices?pageSize=100')
  const inventory = await response.json(), device = inventory.items.find(item => item.enabled && item.isValid)
  check(response.status() === 200 && !!device, 'ACTUAL_REGISTERED_DIAGNOSTIC_TARGET')
  const diagnosticItem = { id: 'mock-icmp-denied', deviceId: device.id, deviceName: device.name, siteId: device.siteId,
    kind: 'PING', state: 'FAILED', outcome: 'ICMP_PERMISSION_DENIED', detail: null, latencyMs: null, evidenceCount: 0, evidenceSources: [] }
  const failed = { id: 'mock-icmp-run', actor: 'superadmin', state: 'FAILED', timeoutMs: 2000, totalDevices: 1, totalChecks: 1, completedChecks: 1,
    succeededChecks: 0, failedChecks: 1, noEvidenceChecks: 0, cancelledChecks: 0, queuedChecks: 0, runningChecks: 0, progressPercent: 100, createdMs: Date.now(), items: [diagnosticItem] }
  const summary = { ...failed, items: undefined, kinds: ['PING'] }
  await page.route('**/api/system/diagnostics/runs**', async route => {
    const request = route.request(), url = new URL(request.url())
    let body
    if (request.method() === 'POST') {
      const payload = request.postDataJSON(); result.submissions.push(payload)
      body = { ...failed, id: 'mock-tcp-run', state: 'SUCCEEDED', failedChecks: 0, succeededChecks: 1,
        items: [{ ...diagnosticItem, id: 'mock-tcp-item', kind: 'TCP', state: 'SUCCEEDED', outcome: 'TCP_CONNECTED', detail: 'TCP 端口已连接（浏览器交互夹具）', latencyMs: 2 }] }
    } else body = url.pathname.endsWith('/mock-icmp-run') ? failed : [summary]
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    const before = result.submissions.length
    await page.goto(base + '/tools', { waitUntil: 'networkidle' })
    await page.getByLabel('诊断历史', { exact: true }).selectOption(failed.id)
    const retry = page.getByRole('button', { name: device.name + ' 改用 TCP 端口检查', exact: true })
    await retry.waitFor()
    check(result.submissions.length === before, `NO_SILENT_TCP_SUBMISSION_${width}`)
    check((await page.locator('body').innerText()).includes('结果只表示该端口是否可连接'), `TCP_SCOPE_EXPLAINED_${width}`)
    await page.screenshot({ path: path.join(output, `icmp-explicit-alternative-${width}.png`), fullPage: true })
    await retry.click()
    await page.getByText('TCP 端口已连接（浏览器交互夹具）', { exact: true }).waitFor()
    check(result.submissions.length === before + 1 && JSON.stringify(result.submissions.at(-1)) === JSON.stringify({ deviceIds: [device.id], kinds: ['TCP'], timeoutMs: 2000 }), `EXPLICIT_EXACT_TCP_PAYLOAD_${width}`)
    check(await retry.count() === 0, `TCP_RESULT_REPLACES_FAILED_VIEW_${width}`)
    const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ view: 'mocked-icmp-tcp', width, scrollWidth })
    check(scrollWidth <= width + 1, `TOOLS_NO_OVERFLOW_${width}`)
    await page.screenshot({ path: path.join(output, `explicit-tcp-result-${width}.png`), fullPage: true })
  }
  check(result.errors.length === 0 && result.deviceMutations.length === 0, 'NO_BROWSER_ERRORS_OR_DEVICE_MUTATIONS')
  result.state = 'PASS'
} catch (error) { result.state = 'FAIL'; result.errors.push(String(error)); process.exitCode = 1 }
finally {
  if (page && result.state === 'FAIL') await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  await browser.close(); save(); console.log(output)
}
