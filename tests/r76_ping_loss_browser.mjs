// Historical evidence in actual Chrome + Service. No mocked HTTP or probes.
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { mkdirSync, realpathSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const assert = (value, message) => { if (!value) throw new Error(message) }
function argument(name) {
  const index = process.argv.indexOf(name)
  assert(index >= 0 && process.argv[index + 1], `Required ${name}`)
  return realpathSync(process.argv[index + 1])
}
const dll = argument('--service-dll'), testsDll = argument('--tests-dll')
const relative = path.relative(realpathSync(workspacePath('build')), dll)
assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'Candidate must be in Workspace/build')
const repository = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const require = createRequire(path.join(repository, 'web/package.json'))
const { chromium } = require('playwright')
const identity = `r76-ping-loss-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`
const output = workspacePath('reports', identity), root = workspacePath('test-data', identity)
mkdirSync(output, { recursive: true })
const result = { status: 'RUNNING', evidenceLevel: 'SYNTHETIC_SEALED_RAW_REAL_HTTP_BROWSER', serviceDll: dll, testsDll, root,
  deviceConnections: 0, deviceConfigurationWrites: 0, checks: [], layouts: [], pageErrors: [], mutations: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const check = (condition, label) => { assert(condition, label); result.checks.push(label); save(); console.log(`${label}=PASS`) }
const bootstrap = String.raw`
import json, sys
from pathlib import Path
sys.path.insert(0, str(Path.cwd() / 'tests'))
from r76_ping_loss_http_e2e import prepare, task_count
from r72_feature_http_e2e import start, stop
dll, tests, root, output = map(Path, sys.argv[1:])
process = None
try:
    fixture = prepare(dll, tests, root, output)
    before = task_count(root)
    process, base = start(dll, root, output)
    print('READY=' + json.dumps(dict(base=base, pid=process.pid, fixture=fixture)), flush=True)
    sys.stdin.readline()
    assert task_count(root) == before, 'Browser history reads created tasks'
    print('TASK_COUNT_UNCHANGED=PASS', flush=True)
finally:
    if process is not None:
        stop(process)
`
let child, browser, page, context, serviceOutput = ''
async function fixtureStart() {
  child = spawn(process.env.PYTHON || 'python', ['-u', '-c', bootstrap, dll, testsDll, root, output],
    { cwd: repository, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  return await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Fixture readiness timed out')), 120000)
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
try {
  save()
  const ready = await fixtureStart(), { base, fixture } = ready
  Object.assign(result, ready)
  browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  context = await browser.newContext({ viewport: { width: 1920, height: 1080 } })
  page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => result.pageErrors.push(error.message))
  page.on('request', request => {
    if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method()))
      result.mutations.push({ method: request.method(), pathname: new URL(request.url()).pathname })
  })
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.goto(base + '/tools', { waitUntil: 'networkidle' })
    await page.getByLabel('诊断历史', { exact: true }).selectOption(fixture.runId)
    const section = page.getByRole('region', { name: 'Long Ping 丢包历史证据', exact: true })
    await section.waitFor()
    const select = section.getByLabel('选择丢失采样', { exact: true })
    const choices = await select.locator('option').evaluateAll(nodes => nodes.map(node => node.value))
    check(choices.includes(String(fixture.sequence)) && !choices.includes(String(fixture.repliedSequence)), `LOSS_${width}_ONLY_LOST_SENT_SAMPLES_SELECTABLE`)
    await select.selectOption(String(fixture.sequence))
    const pending = page.waitForResponse(response => response.url().includes('/loss-evidence/') && response.request().method() === 'GET')
    await section.getByRole('button', { name: '查看采样前证据', exact: true }).click()
    const response = await pending, snapshot = await response.json()
    check(response.status() === 200 && response.headers()['cache-control'] === 'no-store', `LOSS_${width}_ACTUAL_HTTP_NO_STORE`)
    check(snapshot.runId === fixture.runId && snapshot.itemId === fixture.itemId && snapshot.sequence === fixture.sequence &&
      snapshot.deviceId === fixture.deviceId && snapshot.siteId === fixture.siteId && snapshot.connectedApState === 'UNKNOWN' &&
      snapshot.facts.some(fact => fact.id === fixture.expectedFactId) && !snapshot.facts.some(fact => fact.id === fixture.futureFactId) &&
      snapshot.facts.every(fact => fact.capturedMs <= fact.collectionEndedMs && fact.collectionEndedMs <= snapshot.sampleCapturedMs &&
        fact.ageMs === snapshot.sampleCapturedMs - fact.capturedMs), `LOSS_${width}_EXACT_ASOF_BOUNDARY_AND_UNKNOWN_AP`)
    await section.getByText('接入 AP：未知（UNKNOWN）', { exact: true }).waitFor()
    const details = section.locator('details').first()
    await details.locator('summary').click()
    check((await section.innerText()).includes(fixture.expectedFactId) && !(await section.innerText()).includes(fixture.futureFactId), `LOSS_${width}_RAW_PROVENANCE_VISIBLE_FUTURE_ABSENT`)
    const raw = snapshot.facts.find(fact => fact.id === fixture.expectedFactId).rawReferences[0]
    check((await section.innerText()).includes(raw.outputSha256) && (await section.innerText()).includes(raw.segmentSha256), `LOSS_${width}_RAW_HASH_REFERENCES_VISIBLE`)
    await page.screenshot({ path: path.join(output, `loss-evidence-${width}.png`), fullPage: true })
    const actual = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ width, actual })
    check(actual <= width + 1, `LOSS_${width}_NO_HORIZONTAL_OVERFLOW`)
    await select.selectOption('')
    check(await section.locator('.loss-result').count() === 0, `LOSS_${width}_SELECTION_CLEAR_REMOVES_OLD_RESULT`)
    await page.reload({ waitUntil: 'networkidle' })
    check(await page.locator('.loss-result').count() === 0, `LOSS_${width}_RELOAD_DOES_NOT_REPLAY_EVIDENCE`)
  }
  check(result.pageErrors.length === 0, 'NO_BROWSER_ERRORS')
  check(result.mutations.length === 1 && result.mutations[0].pathname === '/api/auth/login', 'HISTORY_UI_ONLY_AUTH_MUTATION_NO_PROBE')
  result.status = 'PASS'
} catch (error) { result.status = 'FAIL'; result.error = String(error); process.exitCode = 1 }
finally {
  if (page && result.status === 'FAIL') await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  if (browser) await browser.close()
  if (child && child.exitCode === null) {
    child.stdin.end('stop\n')
    await new Promise(resolve => { const timer = setTimeout(resolve, 30000); child.once('exit', () => { clearTimeout(timer); resolve() }) })
  }
  result.fixtureStopped = !child || child.exitCode !== null
  result.fixtureExitCode = child?.exitCode
  if (!result.fixtureStopped || (result.fixtureExitCode !== undefined && result.fixtureExitCode !== 0)) {
    result.status = 'FAIL'; result.shutdownError = 'Fixture lifecycle or task-count check failed'; process.exitCode = 1
  }
  result.taskCountUnchanged = serviceOutput.includes('TASK_COUNT_UNCHANGED=PASS')
  save(); console.log('PING_LOSS_BROWSER_REPORT=' + path.join(output, 'result.json'))
}
