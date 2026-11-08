// Actual candidate HTTP and Chrome; configuration evidence is explicitly synthetic.
import { spawn } from 'node:child_process'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { realpathSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const index = process.argv.indexOf('--service-dll')
if (index < 0) throw new Error('--service-dll required')
const dll = realpathSync(process.argv[index + 1])
const relative = path.relative(realpathSync(workspacePath('build')), dll)
if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Workspace candidate required')
const output = workspacePath('reports', `r78-config-fault-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const result = { evidenceLevel: 'FIXTURE_SERVICE_BROWSER', dll, checks: [], errors: [], mutations: [], layouts: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
const check = (value, label) => { if (!value) throw new Error(label); result.checks.push(label); save(); console.log(label + '=PASS') }
let child, browser, page, fixtureOutput = ''
try {
  child = spawn(process.env.PYTHON || 'python', ['-u', 'tests/r78_configuration_fault_http.py', '--service-dll', dll, '--browser'],
    { cwd: path.resolve(import.meta.dirname, '..'), windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  const ready = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Fixture readiness timeout')), 90000)
    const data = chunk => {
      fixtureOutput += chunk.toString(); writeFileSync(path.join(output, 'fixture.log'), fixtureOutput)
      const match = fixtureOutput.match(/^READY=(.+)$/m)
      if (match) { clearTimeout(timer); resolve(JSON.parse(match[1])) }
    }
    child.stdout.on('data', data); child.stderr.on('data', data)
    child.once('error', error => { clearTimeout(timer); reject(error) })
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Fixture exited ${code}: ${fixtureOutput}`)) })
  })
  Object.assign(result, ready)
  browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  const context = await browser.newContext({ acceptDownloads: true })
  // Only explicit login may mutate the fixture. Browsing may never admit device work.
  await context.route('**/api/**', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname
    if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) {
      result.mutations.push(pathname)
      if (pathname !== '/api/auth/login') return route.abort('blockedbyclient')
    }
    return route.continue()
  })
  page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => result.errors.push(error.message))
  await page.goto(ready.base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  const faults = page.getByRole('region', { name: '持久故障中心', exact: true })
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await faults.getByLabel('故障类型', { exact: true }).selectOption({ label: '配置变化' })
    const responsePending = page.waitForResponse(response => {
      const url = new URL(response.url())
      return url.pathname === '/api/system/operations/faults' && url.searchParams.get('kind') === 'CONFIGURATION_CHANGED'
    })
    await faults.getByRole('button', { name: '查询故障', exact: true }).click()
    const response = await responsePending, body = await response.json()
    check(response.status() === 200 && body.total === 1 && body.items[0].id === ready.faultId, `EXACT_FILTERED_API_${width}`)
    await faults.getByText('CONFIGURATION_CHANGED', { exact: true }).waitFor()
    check(await faults.getByRole('cell').filter({ hasText: '待处理' }).count() === 1 &&
      await faults.getByLabel('故障状态', { exact: true }).locator('option[value="OPEN"]').innerText() === '待处理', `READABLE_FAULT_STATE_${width}`)
    check((await faults.innerText()).includes('配置已稳定，不表示已回滚'), `STABILITY_EXPLANATION_${width}`)
    const link = faults.getByRole('link', { name: '查看脱敏配置证据', exact: true })
    check((await link.getAttribute('href')).includes('/backups/after/view?profileId=r73-storage-profile'), `EXACT_BACKUP_LINK_${width}`)
    const evidence = await context.request.get(ready.base + await link.getAttribute('href'))
    check(evidence.status() === 200 && (await evidence.text()).includes('description changed'), `ACTUAL_REDACTED_EVIDENCE_${width}`)
    const instruction = faults.getByText('请在此设备配置历史中采集完整备份，再核验配置是否稳定。', { exact: true })
    if (!await instruction.isVisible()) await faults.getByRole('button', { name: '查看处理历史', exact: true }).click()
    await instruction.waitFor()
    check((await faults.innerText()).includes('采集完整备份') &&
      await faults.getByRole('button', { name: '启用有限只读补采', exact: true }).count() === 0, `CORRECT_CONFIG_ACTION_${width}`)
    const downloaded = page.waitForEvent('download')
    await faults.getByRole('button', { name: '导出当前页故障与历史 CSV', exact: true }).click()
    const download = await downloaded, target = path.join(output, `faults-${width}.csv`)
    await download.saveAs(target)
    const csv = readFileSync(target, 'utf8')
    check(csv.includes('CONFIGURATION_CHANGED') && csv.includes(ready.faultId) && csv.includes('CONFIG_COMPLETE_BACKUP') &&
      !csv.includes('description changed'), `CSV_EVIDENCE_WITHOUT_CONFIG_BODY_${width}`)
    await page.screenshot({ path: path.join(output, `config-fault-${width}.png`), fullPage: true })
    const actual = await page.evaluate(() => document.documentElement.scrollWidth)
    result.layouts.push({ width, actual }); check(actual <= width + 1, `NO_OVERFLOW_${width}`)
    const deviceLink = faults.getByRole('link', { name: '查看故障设备详情', exact: true })
    const deviceUrl = new URL(await deviceLink.getAttribute('href'), ready.base)
    check(deviceUrl.pathname === '/devices' && deviceUrl.searchParams.get('deviceId') === body.items[0].deviceId &&
      !deviceUrl.searchParams.has('q'), `EXACT_FAULT_DEVICE_LINK_${width}`)
    const detailResponsePending = page.waitForResponse(value => new URL(value.url()).pathname ===
      '/api/system/devices/' + encodeURIComponent(body.items[0].deviceId) && value.request().method() === 'GET')
    await deviceLink.click()
    const detailResponse = await detailResponsePending, detail = await detailResponse.json()
    await page.getByRole('dialog', { name: '设备详情 / 审计', exact: true }).waitFor()
    check(detailResponse.ok() && detail.item.id === body.items[0].deviceId, `FAULT_DEVICE_DETAIL_${width}`)
    await page.screenshot({ path: path.join(output, `fault-device-${width}.png`), fullPage: true })
    await page.goto(ready.base + '/', { waitUntil: 'networkidle' })
  }
  check(result.errors.length === 0 && result.mutations.length === 1 && result.mutations[0] === '/api/auth/login', 'NO_ERRORS_OR_DEVICE_MUTATIONS')
  result.state = 'PASS'
} catch (error) { result.state = 'FAIL'; result.errors.push(String(error)); process.exitCode = 1 }
finally {
  if (page && result.state === 'FAIL') await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {})
  if (browser) await browser.close()
  if (child && child.exitCode === null) {
    child.stdin.end('stop\n')
    await new Promise(resolve => { const timer = setTimeout(resolve, 30000); child.once('exit', () => { clearTimeout(timer); resolve() }) })
  }
  result.fixtureExitCode = child?.exitCode
  if (result.fixtureExitCode !== 0) { result.state = 'FAIL'; process.exitCode = 1 }
  save(); console.log(output)
}
