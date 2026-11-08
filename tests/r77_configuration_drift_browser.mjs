// Actual candidate HTTP and Chrome, synthetic backups, no device connections.
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { mkdirSync, realpathSync, writeFileSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const index = process.argv.indexOf('--service-dll')
if (index < 0) throw new Error('--service-dll required')
const dll = realpathSync(process.argv[index + 1])
const repository = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const relative = path.relative(realpathSync(workspacePath('build')), dll)
if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Workspace candidate required')
const require = createRequire(path.join(repository, 'web/package.json'))
const { chromium } = require('playwright')
const name = `r77-drift-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`
const output = workspacePath('reports', name), root = workspacePath('test-data', name)
mkdirSync(output, { recursive: true })
const result = { state: 'RUNNING', evidenceLevel: 'FIXTURE_SERVICE_BROWSER', dll, root, checks: [], layouts: [], mutations: [], pageErrors: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const check = (value, label) => { if (!value) throw new Error(label); result.checks.push(label); save(); console.log(label + '=PASS') }
const bootstrap = String.raw`
import json, sys, time
from pathlib import Path
sys.path.insert(0, str(Path.cwd() / 'tests'))
from r72_feature_http_e2e import start, stop, seed_catalog
from r77_configuration_drift_http import manifest, PROFILE, BASELINE
from r76_ping_loss_http_e2e import task_count
dll, root, output = map(Path, sys.argv[1:])
process = None
try:
    process, _ = start(dll, root, output)
    stop(process)
    process = None
    now = int(time.time()*1000)
    seed_catalog(root, now)
    directory = root / 'backup/config-complete' / PROFILE
    manifest(directory, 'before', 1, BASELINE, now-200)
    manifest(directory, 'after', 2, BASELINE.replace('description original', 'description changed'), now-100)
    before = task_count(root)
    process, base = start(dll, root, output, h3c_enabled=True)
    print('READY=' + json.dumps(dict(base=base, pid=process.pid)), flush=True)
    sys.stdin.readline()
    assert task_count(root) == before, 'UI reads created device tasks'
    print('TASK_COUNT_UNCHANGED=PASS', flush=True)
finally:
    if process is not None: stop(process)
`
let child, browser, page, serviceOutput = ''
try {
  save()
  child = spawn(process.env.PYTHON || 'python', ['-u', '-c', bootstrap, dll, root, output],
    { cwd: repository, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  const ready = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Fixture readiness timeout')), 90000)
    const data = chunk => {
      serviceOutput += chunk.toString(); writeFileSync(path.join(output, 'fixture.log'), serviceOutput)
      const match = serviceOutput.match(/^READY=(.+)$/m)
      if (match) { clearTimeout(timer); resolve(JSON.parse(match[1])) }
    }
    child.stdout.on('data', data); child.stderr.on('data', data)
    child.once('error', error => { clearTimeout(timer); reject(error) })
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Fixture exited ${code}: ${serviceOutput}`)) })
  })
  Object.assign(result, ready)
  browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
  page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => result.pageErrors.push(error.message))
  page.on('response', async response => {
    if (response.url().endsWith('/api/system/modules')) result.modulesResponse = { status: response.status(), type: response.headers()['content-type'], body: await response.text() }
  })
  page.on('request', request => {
    if (!['GET','HEAD','OPTIONS'].includes(request.method())) result.mutations.push({ method: request.method(), pathname: new URL(request.url()).pathname })
  })
  await page.goto(ready.base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.goto(ready.base + '/config-backups?deviceId=r73-storage-device', { waitUntil: 'networkidle' })
    const section = page.getByRole('region', { name: '模拟器完整配置', exact: true })
    await section.getByRole('button', { name: '刷新完整备份', exact: true }).click()
    const drift = section.getByLabel('最近配置变化检测', { exact: true })
    await drift.getByText('最近两次采集发现配置变化', { exact: true }).waitFor()
    check((await drift.innerText()).includes('v1 → v2') && (await drift.innerText()).includes('INTERFACE'), `DRIFT_${width}_VERIFIED_PAIR`)
    const pending = page.waitForResponse(response => response.url().includes('/simulator-config/compare?'))
    await drift.getByRole('button', { name: '查看最近配置差异', exact: true }).click()
    const response = await pending, comparison = await response.json()
    check(response.status() === 200 && comparison.before.id === 'before' && comparison.after.id === 'after', `DRIFT_${width}_EXACT_HTTP_PAIR`)
    const comparisonView = section.getByLabel('配置比较结果', { exact: true })
    await comparisonView.waitFor()
    check((await comparisonView.innerText()).includes('description original') && (await comparisonView.innerText()).includes('description changed'), `DRIFT_${width}_REAL_DIFF_RENDERED`)
    check(await section.getByLabel('比较源备份', { exact: true }).inputValue() === 'before' && await section.getByLabel('比较目标备份', { exact: true }).inputValue() === 'after', `DRIFT_${width}_SELECTORS_MATCH`)
    const downloaded = page.waitForEvent('download')
    await section.getByRole('button', { name: '导出脱敏比较报告', exact: true }).click()
    const download = await downloaded, target = path.join(output, `diff-${width}.json`)
    await download.saveAs(target)
    const exported = JSON.parse(readFileSync(target, 'utf8'))
    check(exported.before.id === 'before' && exported.after.id === 'after' && exported.originalConfigurationChanged, `DRIFT_${width}_ACTUAL_EXPORT`)
    await page.screenshot({ path: path.join(output, `drift-${width}.png`), fullPage: true })
    const actual = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ width, actual }); check(actual <= width + 1, `DRIFT_${width}_NO_OVERFLOW`)
    await page.reload({ waitUntil: 'networkidle' })
    check(await section.getByLabel('配置比较结果', { exact: true }).count() === 0, `DRIFT_${width}_RELOAD_CLEARS_OLD_DIFF`)
  }
  check(result.pageErrors.length === 0, 'NO_BROWSER_ERRORS')
  check(result.mutations.length === 1 && result.mutations[0].pathname === '/api/auth/login', 'ONLY_LOGIN_MUTATION_NO_DEVICE_OPERATIONS')
  result.state = 'PASS'
} catch (error) { result.state = 'FAIL'; result.error = String(error); process.exitCode = 1 }
finally {
  if (page && result.state === 'FAIL') await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  if (browser) await browser.close()
  if (child && child.exitCode === null) {
    child.stdin.end('stop\n')
    await new Promise(resolve => { const timer = setTimeout(resolve, 30000); child.once('exit', () => { clearTimeout(timer); resolve() }) })
  }
  result.taskCountUnchanged = serviceOutput.includes('TASK_COUNT_UNCHANGED=PASS')
  result.fixtureExitCode = child?.exitCode
  if (!result.taskCountUnchanged || result.fixtureExitCode !== 0) { result.state = 'FAIL'; process.exitCode = 1 }
  save(); console.log('DRIFT_BROWSER_REPORT=' + path.join(output, 'result.json'))
}
