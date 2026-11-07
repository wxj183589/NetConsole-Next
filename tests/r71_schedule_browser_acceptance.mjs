import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

// One real SW read-only capture, entirely through the authenticated product UI.
// Keeps its own paused plan for later review; never edits due times or credentials.
const ui = process.env.R71_UI_URL || 'http://127.0.0.1:5174'
const api = process.env.R71_API_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', 'r71-schedule-browser-' + new Date().toISOString().replace(/[:.]/g, '-'))
mkdirSync(output, { recursive: true })
const report = { startedAt: new Date().toISOString(), configurationWrites: false, dueClockEdited: false, errors: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
page.setDefaultTimeout(25000)
let planId
let authenticated = false
page.on('pageerror', error => report.errors.push(error.message))
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const url = new URL(route.request().url())
  if (/^\/api\/system\/config-backup-schedules\/[^/]+\/run$/.test(url.pathname) && url.pathname !== `/api/system/config-backup-schedules/${planId}/run`) {
    report.errors.push('Unexpected plan run blocked')
    await route.abort('blockedbyclient')
    return
  }
  const headers = { ...route.request().headers() }
  if (headers.origin) headers.origin = api
  await route.fulfill({ response: await route.fetch({ url: api + url.pathname + url.search, headers, timeout: 240000 }) })
})
const assert = (value, message) => { if (!value) throw new Error(message) }
const read = target => page.evaluate(async url => {
  const response = await fetch(url)
  if (!response.ok) throw new Error('HTTP_' + response.status)
  return response.json()
}, target)
try {
  await page.goto(ui, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill('superadmin')
  await page.getByLabel('密码').fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  authenticated = true
  await page.goto(ui + '/config-backups', { waitUntil: 'networkidle' })
  const bulk = page.getByRole('region', { name: '完整配置批量与定时备份' })
  const targetRow = page.locator('.check-row').filter({ has: page.locator('input[value="c18dcb225dda4d059ce9799a71a28b2b"]') })
  assert(await targetRow.count() === 1, 'SW selection must be exact and unambiguous')
  await targetRow.locator('input[type=checkbox]').check()
  const previewResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/system/complete-configuration/targets' && r.request().method() === 'POST')
  await bulk.getByRole('button', { name: '预览完整备份目标', exact: true }).click()
  const preview = await (await previewResponse).json()
  assert(preview.profileIds.length === 1 && preview.profileIds[0] === 'c18dcb225dda4d059ce9799a71a28b2b', 'Preview must contain only the authorized SW')
  report.preview = preview
  await bulk.getByLabel('完整备份周期', { exact: true }).selectOption('60')
  const createdResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/system/config-backup-schedules' && r.request().method() === 'POST')
  const refreshedPlans = page.waitForResponse(r => new URL(r.url()).pathname === '/api/system/config-backup-schedules' && r.request().method() === 'GET')
  await bulk.getByRole('button', { name: '创建完整配置计划', exact: true }).click()
  const created = await createdResponse
  assert(created.status() === 201, 'UI plan creation failed')
  report.createdPlan = await created.json()
  planId = report.createdPlan.id
  assert((await (await refreshedPlans).json())[0].id === planId, 'New plan must be first before selecting its action')
  const planRows = bulk.locator('.table-wrap').last().locator('tbody > tr')
  const row = planRows.first()
  await row.getByRole('button', { name: '立即执行', exact: true }).waitFor()
  const runResponse = page.waitForResponse(r => new URL(r.url()).pathname === `/api/system/config-backup-schedules/${planId}/run` && r.request().method() === 'POST')
  await row.getByRole('button', { name: '立即执行', exact: true }).click()
  const admittedResponse = await runResponse
  assert(admittedResponse.status() === 202, 'UI run-now admission failed')
  report.admittedPlan = await admittedResponse.json()
  assert(report.admittedPlan.nextRunMs === report.createdPlan.nextRunMs && report.admittedPlan.enabled, 'Manual execution must preserve cadence')
  await bulk.locator('[aria-label="完整配置批量进度"]').getByText('成功 1 · 失败 0', { exact: false }).waitFor({ timeout: 180000 })
  const batch = await read('/api/system/config-backups/batches/' + report.admittedPlan.lastBatchId)
  assert(batch.state === 'SUCCEEDED' && batch.total === 1 && batch.succeeded === 1, 'Real scheduled read-only backup failed')
  report.batch = batch
  const snapshot = batch.items[0].snapshotId
  const view = await read('/api/system/simulator-config/backups/' + snapshot + '/view')
  for (const [extension, digest, count] of [['.cfg', view.backup.rawSha256, view.backup.rawByteCount], ['.view.cfg', view.backup.redactedSha256, view.backup.redactedByteCount]]) {
    const data = readFileSync(workspacePath('test-data', 'simulator-live-20261105', 'backup', 'config-complete', preview.profileIds[0], snapshot + extension))
    assert(data.length === count && createHash('sha256').update(data).digest('hex') === digest, 'Stored private/redacted backup integrity mismatch')
  }
  report.privateAndRedactedIntegrityVerified = true
  const deadline = Date.now() + 45000
  while (true) {
    const plans = await read('/api/system/config-backup-schedules')
    const current = plans.find(item => item.id === planId)
    if (current.lastState === 'SUCCEEDED') { report.observedPlan = current; break }
    assert(Date.now() < deadline, 'Background did not persist terminal schedule state')
    await page.waitForTimeout(500)
  }
  await bulk.getByRole('button', { name: '刷新完整配置计划', exact: true }).click()
  const pausedResponse = page.waitForResponse(r => new URL(r.url()).pathname === `/api/system/config-backup-schedules/${planId}` && r.request().method() === 'PATCH')
  await row.getByRole('button', { name: '暂停', exact: true }).click()
  report.pausedPlan = await (await pausedResponse).json()
  assert(!report.pausedPlan.enabled && report.pausedPlan.nextRunMs === null, 'UI pause did not persist')
  await row.getByRole('button', { name: '启用', exact: true }).waitFor()
  await row.locator('summary').click()
  assert((await row.innerText()).includes(preview.profileIds[0]), 'Frozen plan targets missing')
  const recentResponse = page.waitForResponse(r => new URL(r.url()).pathname === `/api/system/config-backups/batches/${batch.id}`)
  await row.getByRole('button', { name: '查看最近执行', exact: true }).click()
  report.recentBatch = await (await recentResponse).json()
  assert(report.recentBatch.id === batch.id, 'Recent execution must read actual schedule batch')
  await bulk.getByText(`计划 ${planId} 最近执行 · 批次 ${batch.id}`, { exact: true }).waitFor()
  report.layouts = []
  for (const viewport of [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
    await page.setViewportSize(viewport)
    await page.screenshot({ path: path.join(output, 'schedule-recent-' + viewport.width + '.png'), fullPage: true })
    const layout = await page.evaluate(() => ({ viewport: innerWidth, document: document.documentElement.scrollWidth }))
    assert(layout.document <= layout.viewport + 1, 'Horizontal page overflow')
    report.layouts.push(layout)
  }
  assert(report.errors.length === 0, 'Browser page errors')
  report.validation = 'PASS_UI_CREATE_RUN_PAUSE_RECENT_REAL_SW'
} catch (error) {
  report.validation = 'FAILED'
  report.error = String(error)
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
} finally {
  // Own only the plan created above; never mutate another plan or the natural-due AC1 plan.
  if (planId && authenticated && !report.pausedPlan) {
    report.pauseFallback = await page.evaluate(async id => {
      const { csrfToken } = await (await fetch('/api/auth/csrf')).json()
      const response = await fetch('/api/system/config-backup-schedules/' + id, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'X-NC-CSRF': csrfToken }, body: JSON.stringify({ enabled: false }) })
      return { status: response.status }
    }, planId).catch(() => ({ status: 'TRANSPORT_FAILED' }))
  }
  report.completedAt = new Date().toISOString()
  writeFileSync(path.join(output, 'result.json'), JSON.stringify(report, null, 2))
  await page.unrouteAll({ behavior: 'ignoreErrors' })
  await browser.close()
}
console.log(JSON.stringify({ output, validation: report.validation, planId, error: report.error }))
process.exitCode = report.validation === 'FAILED' ? 1 : 0
