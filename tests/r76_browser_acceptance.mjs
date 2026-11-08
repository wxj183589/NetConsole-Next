// Real browser read/export acceptance. No fixtures, request fulfilment, or device operations.
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R76_UI_URL || 'http://127.0.0.1:5174'
const output = workspacePath('reports', process.env.R76_BROWSER_OUTPUT || `r76-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const result = { base, evidenceLevel: 'SERVICE_E2E_BROWSER_READONLY', startedUtc: new Date().toISOString(),
  deviceConnections: 0, configurationWrites: 0, checks: [], layouts: [], apiResponses: [], blockedMutations: [], errors: [] }
const assert = (condition, message) => { if (!condition) throw new Error(message) }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\Program Files\Google\Chrome\Application\chrome.exe' })
const context = await browser.newContext({ acceptDownloads: true })
await context.route('**/api/**', async route => {
  const request = route.request(), pathname = new URL(request.url()).pathname
  if (['GET', 'HEAD', 'OPTIONS'].includes(request.method()) ||
      (request.method() === 'POST' && pathname === '/api/auth/login')) return route.continue()
  result.blockedMutations.push({ method: request.method(), pathname })
  await route.abort('blockedbyclient')
})
const page = await context.newPage()
page.setDefaultTimeout(20000)
page.on('pageerror', error => result.errors.push(error.message))
page.on('response', response => {
  const request = response.request(), pathname = new URL(response.url()).pathname
  if (pathname.startsWith('/api/')) result.apiResponses.push({ method: request.method(), pathname, status: response.status() })
})
async function step(name, width, action) {
  const entry = { name, width, status: 'PASS' }
  try { Object.assign(entry, await action()) }
  catch (error) { entry.status = 'FAIL'; entry.error = String(error) }
  try {
    await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true })
    const actual = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ name, width, actual })
    assert(actual <= width + 1, `${name}: horizontal overflow ${actual}/${width}`)
  } catch (error) { entry.status = 'FAIL'; entry.layoutError = String(error) }
  result.checks.push(entry); save(); console.log(`${width} ${name} ${entry.status}`)
}
async function download(button, filename) {
  const pending = page.waitForEvent('download')
  await button.click()
  const file = await pending, target = path.join(output, filename)
  await file.saveAs(target)
  return readFileSync(target, 'utf8')
}
try {
  await page.goto(base, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill(process.env.R76_USER || 'superadmin')
  await page.getByLabel('密码', { exact: true }).fill(process.env.R76_PASSWORD || 'superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await step('faultFilters', width, async () => {
      await page.goto(base, { waitUntil: 'networkidle' })
      const panel = page.getByRole('region', { name: '持久故障中心', exact: true })
      const filter = panel.getByLabel('故障类型', { exact: true })
      await filter.selectOption('LLDP_NEIGHBOR_LOST')
      const pending = page.waitForResponse(response => { const url = new URL(response.url()); return url.pathname === '/api/system/operations/faults' && url.searchParams.get('kind') === 'LLDP_NEIGHBOR_LOST' })
      await panel.getByRole('button', { name: '查询故障', exact: true }).click()
      const response = await pending
      assert(response.ok(), 'Fault filter HTTP failed')
      const payload = await response.json()
      assert(await filter.inputValue() === 'LLDP_NEIGHBOR_LOST', 'Filter selection lost')
      assert(payload.items.every(item => item.kind === 'LLDP_NEIGHBOR_LOST'), 'Fault response ignores selected kind')
      return { selectedKind: await filter.inputValue(), matchesOnPage: payload.items.length, httpStatus: response.status() }
    })
    await step('wirelessCurrentAcAndExport', width, async () => {
      await page.goto(`${base}/wireless`, { waitUntil: 'networkidle' })
      const members = page.locator('.wireless-tree__member')
      assert(await members.count() > 0, 'No existing AC available for current-AC check')
      await members.first().click()
      const collect = page.getByRole('button', { name: '采集当前 AC', exact: true })
      await collect.waitFor(); assert(await collect.isEnabled(), 'Current AC collection button unavailable')
      const selectedAc = await page.locator('.wireless-task-quick').innerText()
      const exports = []
      const domains = page.locator('.wireless-tree__domain > button:first-child')
      for (let index = 0; index < await domains.count(); index++) {
        await domains.nth(index).click()
        const button = page.getByRole('button', { name: '导出当前 AP 观察 CSV', exact: true })
        if (!await button.isEnabled()) continue
        const csv = await download(button, `wireless-${width}-domain-${index}.csv`)
        assert(['observation_id', 'source_ac_device_id', 'raw_manifest_ref', 'FILTERED_LOADED_OBSERVATIONS_ONLY'].every(value => csv.includes(value)), 'AP CSV provenance missing')
        exports.push({ index, bytes: Buffer.byteLength(csv) })
      }
      assert(exports.length > 0, 'No existing AP observations exported')
      return { selectedAc, collectButtonCheckedWithoutClick: true, exports }
    })
    await step('quickPingP95HistoryCsv', width, async () => {
      await page.goto(`${base}/tools`, { waitUntil: 'networkidle' })
      const panel = page.getByTestId('quick-ping-history-trend')
      await panel.waitFor()
      const load = panel.getByRole('button', { name: '加载 Quick Ping 趋势', exact: true })
      if (!await load.isEnabled()) return { status: 'PARTIAL', reason: 'NO_STORED_QUICK_PING_HISTORY', newProbeStarted: false }
      await load.click()
      await panel.getByRole('button', { name: '加载 Quick Ping 趋势', exact: true }).waitFor()
      const tables = panel.getByRole('table', { name: 'Quick Ping 跨任务趋势', exact: true })
      if (!await tables.count()) return { status: 'PARTIAL', reason: 'NO_COMPLETED_SCOPED_QUICK_PING_POINTS', newProbeStarted: false }
      assert(await tables.first().getByRole('columnheader', { name: 'P95 RTT', exact: true }).isVisible(), 'P95 column missing')
      const csv = await download(panel.getByRole('button', { name: '导出趋势 CSV', exact: true }), `quick-ping-history-${width}.csv`)
      assert(['p95_replied_ms_nearest_rank', 'actor', 'site_id', 'device_id', 'run_id', 'unsent_unknown', 'LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS'].every(value => csv.includes(value)), 'Ping CSV metrics/scope missing')
      assert(csv.trim().split(/\r?\n/).length > 1, 'Ping CSV has no historical points')
      return { trendGroups: await tables.count(), csvBytes: Buffer.byteLength(csv), newProbeStarted: false }
    })
    await step('configurationHistory', width, async () => {
      await page.goto(`${base}/config-backups?deviceId=${encodeURIComponent(process.env.R76_CONFIG_DEVICE || 'r68-ac1')}`, { waitUntil: 'networkidle' })
      await page.getByRole('table', { name: '配置版本历史', exact: true }).waitFor()
      const panel = page.getByRole('region', { name: '完整配置批量与定时备份', exact: true })
      const pending = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/complete-configuration/batches')
      await panel.getByRole('button', { name: '刷新完整备份批次历史', exact: true }).click()
      const response = await pending
      assert(response.ok(), 'Configuration history HTTP failed')
      const history = await response.json(), selection = panel.getByLabel('完整备份批次历史', { exact: true })
      if (history.length) {
        await selection.selectOption(history[0].id)
        const detailPending = page.waitForResponse(response => new URL(response.url()).pathname === `/api/system/config-backups/batches/${history[0].id}`)
        await panel.getByRole('button', { name: '查看批次结果', exact: true }).click()
        const detail = await detailPending
        assert(detail.ok() && (await detail.json()).id === history[0].id, 'Selected batch history identity mismatch')
      } else assert(await panel.getByRole('button', { name: '查看批次结果', exact: true }).isDisabled(), 'Empty history detail must be disabled')
      return { storedBatches: history.length, deviceWriteStarted: false }
    })
  }
} catch (error) { result.errors.push(String(error)) }
finally {
  result.finishedUtc = new Date().toISOString()
  result.overall = result.errors.length || result.blockedMutations.length || result.checks.some(check => check.status === 'FAIL') ? 'FAIL' : result.checks.some(check => check.status === 'PARTIAL') ? 'PARTIAL' : 'PASS'
  save(); await browser.close()
}
console.log(JSON.stringify({ output, overall: result.overall, checks: result.checks, errors: result.errors, blockedMutations: result.blockedMutations }, null, 2))
if (result.overall !== 'PASS') process.exitCode = 1
