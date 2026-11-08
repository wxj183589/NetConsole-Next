// Real Service + Chrome acceptance. Requires the exclusive Service mutex window.
// Usage: node tests/r76_long_ping_browser.mjs --service-dll <candidate Service.dll>
// Owns one minimal loopback fixture; never mocks HTTP or contacts a network device.
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const assert = (value, message) => { if (!value) throw new Error(message) }
const argument = process.argv.indexOf('--service-dll')
assert(argument >= 0 && process.argv[argument + 1], 'Required --service-dll <candidate Service.dll>')
const dll = realpathSync(process.argv[argument + 1])
const relative = path.relative(realpathSync(workspacePath('build')), dll)
assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'Candidate DLL must be in Workspace/build')
const repository = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const require = createRequire(path.join(repository, 'web', 'package.json'))
const { chromium } = require('playwright')
const identity = `r76-long-ping-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`
const output = workspacePath('reports', identity), root = workspacePath('test-data', identity)
mkdirSync(output, { recursive: true })
const result = { status: 'RUNNING', evidenceLevel: 'SERVICE_E2E_BROWSER_LOOPBACK', serviceDll: dll, root,
  deviceConnections: 0, deviceConfigurationWrites: 0, protocol: 'ICMP', target: '127.0.0.1',
  startedUtc: new Date().toISOString(), checks: [], layouts: [], apiResponses: [], pageErrors: [], runs: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const check = (condition, label) => { assert(condition, label); result.checks.push(label); save(); console.log(`${label}=PASS`) }
const bootstrap = String.raw`
import json, sqlite3, sys, time
from contextlib import closing
from pathlib import Path
sys.path.insert(0, str(Path.cwd() / 'tests'))
from r72_feature_http_e2e import start, stop
dll, root, output = map(Path, sys.argv[1:])
process = None
try:
    process, base = start(dll, root, output)
    stop(process)
    process = None
    now = int(time.time() * 1000)
    with closing(sqlite3.connect(root / 'catalog.db')) as db:
        db.execute("INSERT INTO device_inventory(id,name,management_ip,vendor,device_type,site_id,enabled,created_ms,updated_ms) VALUES('r76-browser-loopback','Synthetic browser loopback','127.0.0.1','OTHER','SWITCH','local',1,?,?)", (now, now))
        db.commit()
    process, base = start(dll, root, output)
    print('READY=' + json.dumps(dict(base=base, pid=process.pid)), flush=True)
    sys.stdin.readline()
finally:
    if process is not None:
        stop(process)
`
let serviceOutput = '', browser, context, page, child
let base
const activeRuns = new Set()
const trace = []
async function startFixture() {
  child = spawn(process.env.PYTHON || 'python', ['-u', '-c', bootstrap, dll, root, output],
    { cwd: repository, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  return await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Fixture readiness timed out')), 65000)
    const data = chunk => {
      serviceOutput += chunk.toString(); writeFileSync(path.join(output, 'fixture.log'), serviceOutput)
      const ready = serviceOutput.match(/^READY=(.+)$/m)
      if (ready) { clearTimeout(timer); resolve(JSON.parse(ready[1])) }
    }
    child.stdout.on('data', data); child.stderr.on('data', data)
    child.once('error', error => { clearTimeout(timer); reject(error) })
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Fixture exited ${code}: ${serviceOutput}`)) })
  })
}
async function api(route) {
  const response = await context.request.get(base + route)
  assert(response.ok(), `GET ${route} failed ${response.status()}`)
  return await response.json()
}
async function until(route, predicate, seconds = 20) {
  const deadline = Date.now() + seconds * 1000
  let latest
  while (Date.now() < deadline) {
    latest = await api(route)
    if (predicate(latest)) return latest
    await new Promise(resolve => setTimeout(resolve, 100))
  }
  throw new Error(`State timeout ${route}: ${JSON.stringify(latest)}`)
}
async function screenshot(name, width) {
  await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true })
  const actual = await page.evaluate(() => document.documentElement.scrollWidth)
  result.layouts.push({ name, width, actual })
  check(actual <= width + 1, `${name}_${width}_NO_HORIZONTAL_OVERFLOW`)
}
async function clickMutation(name, pathname, expected = 200) {
  const response = page.waitForResponse(response => new URL(response.url()).pathname === pathname && response.request().method() === 'POST')
  await page.getByRole('button', { name, exact: true }).click()
  const actual = await response
  assert(actual.status() === expected, `${name}: HTTP ${actual.status()} ${await actual.text()}`)
  return await actual.json()
}
async function configure(kind) {
  await page.getByLabel('诊断设备', { exact: true }).selectOption(['r76-browser-loopback'])
  for (const candidate of ['PING', 'TCP', 'DNS', 'TRACEROUTE', 'LLDP', 'QUICK_PING', 'LONG_PING'])
    await page.locator(`input[type=checkbox][value=${candidate}]`).setChecked(candidate === kind)
  await page.getByLabel('每项检查总超时', { exact: true }).selectOption('2000')
  if (kind === 'LONG_PING') {
    await page.getByLabel('长期 Ping 协议', { exact: true }).selectOption('ICMP')
    await page.getByLabel('长期 Ping 采样间隔', { exact: true }).fill('1000')
    await page.getByLabel('长期 Ping 持续时长', { exact: true }).selectOption('60')
  } else {
    await page.getByLabel('Quick Ping 协议', { exact: true }).selectOption('ICMP')
    await page.getByLabel('Quick Ping 采样次数', { exact: true }).fill('2')
    await page.getByLabel('Quick Ping 采样间隔', { exact: true }).fill('100')
  }
}
function csvRecords(text) {
  const records = [], cells = []
  let cell = '', quoted = false
  const input = text.replace(/^﻿/, '')
  for (let index = 0; index < input.length; index++) {
    const ch = input[index]
    if (ch === '"') { if (quoted && input[index + 1] === '"') { cell += '"'; index++ } else quoted = !quoted }
    else if (!quoted && (ch === ',' || ch === '\n')) {
      cells.push(cell); cell = ''; if (ch === '\n') records.push(cells.splice(0))
    } else if (quoted || ch !== '\r') cell += ch
  }
  assert(!quoted, 'CSV quote balance')
  if (cell || cells.length) { cells.push(cell); records.push(cells.splice(0)) }
  const header = records.shift()
  assert(header && new Set(header).size === header.length, 'CSV header unique')
  return records.map(record => {
    assert(record.length === header.length, 'CSV field count')
    return Object.fromEntries(header.map((key, index) => [key, record[index]]))
  })
}
try {
  save()
  const ready = await startFixture(); base = ready.base; result.base = base; result.servicePid = ready.pid
  browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
  context = await browser.newContext({ acceptDownloads: true, viewport: { width: 1920, height: 1080 } })
  page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => result.pageErrors.push(error.message))
  page.on('response', response => {
    const request = response.request(), pathname = new URL(response.url()).pathname
    if (!pathname.startsWith('/api/')) return
    result.apiResponses.push({ method: request.method(), pathname, status: response.status() })
    if (request.method() === 'POST') trace.push({ pathname, payload: pathname === '/api/auth/login' ? { redacted: true } : request.postDataJSON() })
  })
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.goto(base + '/tools', { waitUntil: 'networkidle' })
    await configure('LONG_PING')
    const created = await clickMutation('开始诊断', '/api/system/diagnostics/runs', 202)
    activeRuns.add(created.id)
    const route = `/api/system/diagnostics/runs/${created.id}`
    const running = await until(route, value => value.items[0].quickPing?.received >= 2)
    check(running.items[0].quickPing.destinationAddress === '127.0.0.1', `LONG_PING_${width}_ACTUAL_LOOPBACK_REPLIES`)
    await clickMutation('暂停长期 Ping', route + '/long-ping/pause')
    const paused = await until(route, value => value.items[0].state === 'PAUSED')
    await page.locator('.diagnostic-results').getByText('已暂停', { exact: true }).waitFor()
    await screenshot('long-ping-paused', width)
    await new Promise(resolve => setTimeout(resolve, 2200))
    check(JSON.stringify((await api(route)).items[0].quickPing) === JSON.stringify(paused.items[0].quickPing),
      `LONG_PING_${width}_PAUSE_ADDS_NO_SAMPLE_OR_LOSS`)
    await clickMutation('恢复长期 Ping', route + '/long-ping/resume')
    const resumed = await until(route, value => value.items[0].quickPing.sent > paused.items[0].quickPing.sent)
    check(resumed.items[0].state === 'RUNNING' && resumed.pausedChecks === 0, `LONG_PING_${width}_RESUME_EXISTING_RUN`)
    await screenshot('long-ping-resumed', width)
    await clickMutation('取消任务', route + '/cancel')
    const terminal = await until(route, value => value.endedMs !== null)
    activeRuns.delete(created.id)
    check(terminal.state === 'CANCELLED' && terminal.items[0].quickPing.lost === 0, `LONG_PING_${width}_CANCEL_PRESERVES_REAL_SAMPLES`)
    result.runs.push(terminal)
    await page.getByRole('button', { name: '开始诊断', exact: true }).waitFor({ state: 'visible' })
    await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(button => button.textContent.trim() === '开始诊断')?.disabled)
    await screenshot('long-ping-cancelled', width)
    const starts = trace.filter(item => item.pathname === '/api/system/diagnostics/runs').length
    await page.reload({ waitUntil: 'networkidle' })
    await page.getByLabel('诊断历史', { exact: true }).selectOption(created.id)
    await page.locator('.diagnostic-results').getByText('已取消', { exact: true }).waitFor()
    check(trace.filter(item => item.pathname === '/api/system/diagnostics/runs').length === starts, `LONG_PING_${width}_REOPEN_HISTORY_DOES_NOT_PROBE`)
    await screenshot('long-ping-history', width)
    await configure('QUICK_PING')
    const quick = await clickMutation('开始诊断', '/api/system/diagnostics/runs', 202)
    activeRuns.add(quick.id)
    const saved = await until(`/api/system/diagnostics/runs/${quick.id}`, value => value.endedMs !== null)
    activeRuns.delete(quick.id); result.runs.push(saved)
    check(saved.items[0].quickPing.received === 2 && saved.items[0].quickPing.lost === 0, `QUICK_PING_${width}_CSV_SOURCE_REAL_REPLIES`)
    await page.reload({ waitUntil: 'networkidle' })
    const trend = page.getByTestId('quick-ping-history-trend')
    const beforeCsv = trace.filter(item => item.pathname === '/api/system/diagnostics/runs').length
    await trend.getByRole('button', { name: '加载 Quick / Long Ping 趋势', exact: true }).click()
    const exportButton = trend.getByRole('button', { name: '导出趋势 CSV', exact: true })
    await exportButton.waitFor()
    const downloaded = page.waitForEvent('download'); await exportButton.click()
    const download = await downloaded, destination = path.join(output, `quick-long-ping-history-${width}.csv`)
    await download.saveAs(destination)
    const rows = csvRecords(readFileSync(destination, 'utf8'))
    for (const [source, kind] of [[terminal, 'LONG_PING'], [saved, 'QUICK_PING']]) {
      const matching = rows.filter(item => item.run_id === source.id)
      assert(matching.length === 1, `${kind}: one CSV point for the exact run`)
      const row = matching[0], item = source.items[0], ping = item.quickPing
      const replied = ping.samples.filter(sample => sample.status === 'REPLIED').map(sample => sample.latencyMs).sort((a, b) => a - b)
      const actualP95 = replied[Math.ceil(replied.length * .95) - 1]
      const actualAverage = Math.round(replied.reduce((sum, value) => sum + value, 0) / replied.length * 100) / 100
      check(row.diagnostic_kind === kind && row.item_id === item.id && row.device_id === 'r76-browser-loopback' &&
        row.site_id === 'local' && row.actor === source.actor && row.destination_address === '127.0.0.1' &&
        row.protocol === 'ICMP' && row.state === item.state && Number(row.requested) === ping.requestedSamples &&
        Number(row.sent) === ping.sent && Number(row.received) === ping.received && Number(row.lost) === ping.lost &&
        Number(row.unsent_unknown) === ping.requestedSamples - ping.sent &&
        Number(row.loss_percent_sent_only) === ping.lossPercent && Number(row.average_replied_ms) === actualAverage &&
        Number(row.p95_replied_ms_nearest_rank) === actualP95 && ping.p95Ms === actualP95 &&
        row.metrics_known === 'true' && row.export_scope === 'LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS',
        `${kind}_${width}_CSV_MATCHES_REAL_HTTP_SAMPLES_AND_P95`)
      if (kind === 'LONG_PING') check(Number(row.unsent_unknown) > 0 && Number(row.lost) === 0 &&
        Number(row.loss_percent_sent_only) === 0 && ping.sent === ping.samples.length,
        `LONG_PING_${width}_CSV_PAUSED_AND_CANCELLED_UNSENT_NOT_LOSS`)
    }
    const tables = trend.getByRole('table', { name: 'Quick / Long Ping 跨任务趋势', exact: true })
    check(await tables.count() === 2 && rows.every(row => ['QUICK_PING', 'LONG_PING'].includes(row.diagnostic_kind)),
      `PING_${width}_QUICK_AND_LONG_HISTORY_GROUPS_STAY_SEPARATE`)
    check(trace.filter(item => item.pathname === '/api/system/diagnostics/runs').length === beforeCsv,
      `PING_${width}_HISTORY_LOAD_AND_CSV_EXPORT_DO_NOT_PROBE`)
    await screenshot('quick-long-ping-history-csv', width)
  }
  check(result.pageErrors.length === 0, 'NO_BROWSER_PAGE_ERRORS')
  check(trace.every(item => item.pathname === '/api/auth/login' || /^\/api\/system\/diagnostics\/runs(?:\/[^/]+\/(?:cancel|long-ping\/(?:pause|resume)))?$/.test(item.pathname)), 'ONLY_AUTH_AND_DIAGNOSTIC_UI_MUTATIONS')
  check(trace.filter(item => item.pathname === '/api/system/diagnostics/runs').every(item =>
    JSON.stringify(item.payload.deviceIds) === JSON.stringify(['r76-browser-loopback']) &&
    item.payload.kinds.length === 1 && ['LONG_PING', 'QUICK_PING'].includes(item.payload.kinds[0])), 'ALL_PROBES_EXACT_LOOPBACK_INVENTORY_ONLY')
  result.status = 'PASS'
} catch (error) { result.status = 'FAIL'; result.error = String(error); process.exitCode = 1 }
finally {
  if (page && result.status === 'FAIL') await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  if (browser) await browser.close()
  // Closing stdin asks only our bootstrap to gracefully stop only its owned Service.
  if (child && child.exitCode === null) {
    child.stdin.end('stop\n')
    await new Promise(resolve => { const timer = setTimeout(resolve, 30000); child.once('exit', () => { clearTimeout(timer); resolve() }) })
  }
  result.activeRunIdsAtShutdown = [...activeRuns]
  result.uiPostRequests = trace
  result.fixtureStopped = !child || child.exitCode !== null
  if (!result.fixtureStopped) { result.status = 'FAIL'; result.shutdownError = 'Owned fixture did not stop within 30 seconds'; process.exitCode = 1 }
  result.finishedUtc = new Date().toISOString(); save()
  console.log('LONG_PING_BROWSER_REPORT=' + path.join(output, 'result.json'))
}
