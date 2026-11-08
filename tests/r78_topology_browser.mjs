// Published candidate: real scoped reads plus an explicitly labelled profile-error fixture.
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R78_UI_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', `r78-topology-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const report = { base, evidenceLevel: 'SERVICE_E2E_BROWSER_READONLY', fixtureEvidence: 'BROWSER_UI_PROFILE_503_WITH_REAL_INVENTORY', checks: [], layouts: [], errors: [], blockedMutations: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(report, null, 2))
const check = (value, label) => { if (!value) throw new Error(label); report.checks.push(label); save(); console.log(label + '=PASS') }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
try {
  const context = await browser.newContext({ acceptDownloads: true })
  await context.route('**/api/**', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname
    if (['GET', 'HEAD', 'OPTIONS'].includes(request.method()) || pathname === '/api/auth/login') return route.continue()
    report.blockedMutations.push({ pathname, method: request.method() }); await route.abort('blockedbyclient')
  })
  const page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => report.errors.push(error.message))
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  check((await page.locator('body').innerText()).includes('暂停和取消不提供恢复证据'), 'FAULT_PROTOCOL_PAUSE_EXPLANATION')
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.goto(base + '/topology', { waitUntil: 'networkidle' })
    const selector = page.getByLabel('LLDP 查看范围', { exact: true })
    await selector.waitFor()
    check(await selector.inputValue() === 'false', `CURRENT_DEFAULT_${width}`)
    const pages = page.locator('[aria-label="资产分页"]')
    check(await pages.getByRole('button', { name: '上一页资产', exact: true }).isDisabled(), `FIRST_ASSET_PAGE_${width}`)
    const pending = []
    const onResponse = response => {
      const url = new URL(response.url())
      if (url.pathname.endsWith('/lldp-claims') && url.searchParams.get('history') === 'true') {
        pending.push(response.json().then(body => ({ status: response.status(), body })))
      }
    }
    page.on('response', onResponse)
    await selector.selectOption({ label: '有效变化历史' })
    await page.getByRole('heading', { name: 'LLDP 有效变化历史', exact: true }).waitFor()
    await page.waitForFunction(() => document.querySelector('select[aria-label="LLDP 查看范围"]')?.disabled === false)
    await page.waitForLoadState('networkidle')
    page.off('response', onResponse)
    const loaded = await Promise.all(pending)
    check(loaded.length > 0 && loaded.every(value => value.status === 200), `ACTUAL_HISTORY_HTTP_${width}`)
    const expected = loaded.flatMap(value => value.body.relations).filter(row => row.changeKind !== 'REPEAT')
    const table = page.getByRole('table', { name: 'LLDP 关系表', exact: true })
    const actual = await table.locator('tbody tr').count()
    report.historyRead = { width, actual, expected: expected.length, profiles: loaded.map(value => ({ profileId: value.body.profileId, rows: value.body.relations.length })), rendered: await table.innerText() }
    check(actual === (expected.length || 1), `HISTORY_MATCHES_SCOPED_API_${width}`)
    check(!(await table.innerText()).includes('REPEAT'), `REPEATED_OBSERVATIONS_HIDDEN_${width}`)
    const exportButton = page.getByRole('button', { name: '导出当前筛选 LLDP CSV', exact: true })
    if (expected.length) {
      const download = page.waitForEvent('download'); await exportButton.click()
      const file = await download, target = path.join(output, `history-${width}.csv`)
      await file.saveAs(target)
      const csv = readFileSync(target, 'utf8')
      check(csv.includes('fact_revision') && csv.includes('source_authority') && expected.every(row => csv.includes(row.factRevision)), `HISTORY_CSV_PROVENANCE_${width}`)
    } else check(await exportButton.isDisabled(), `EMPTY_HISTORY_EXPORT_DISABLED_${width}`)
    check((await page.locator('body').innerText()).includes('历史不代表当前链路健康'), `HISTORY_BOUNDARY_VISIBLE_${width}`)
    await page.screenshot({ path: path.join(output, `topology-history-${width}.png`), fullPage: true })
    const actualWidth = await page.evaluate(() => document.documentElement.scrollWidth)
    report.layouts.push({ width, actualWidth, rows: expected.length, profiles: loaded.length })
    check(actualWidth <= width + 1, `NO_OVERFLOW_${width}`)
    const assets = page.locator('section.card').filter({ has: page.getByRole('heading', { name: '资产清单', exact: true }) }).getByRole('table')
    const inventoryText = await assets.innerText()
    await page.route('**/api/system/h3c/profiles', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"explicit profile failure fixture"}' }))
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await page.getByText('连接档案读取失败，LLDP 关系状态未知。已保留成功读取的资产与关系。', { exact: true }).waitFor()
    check(await assets.innerText() === inventoryText, `FIXTURE_PROFILE_FAILURE_RETAINS_REAL_ASSETS_${width}`)
    check(!(await page.locator('body').innerText()).includes('拓扑与资产读取失败'), `FIXTURE_PROFILE_FAILURE_NOT_WORKSPACE_FAILURE_${width}`)
    await page.screenshot({ path: path.join(output, `profile-failure-${width}.png`), fullPage: true })
    await page.unroute('**/api/system/h3c/profiles')
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await page.waitForLoadState('networkidle')
    check(!(await page.locator('body').innerText()).includes('连接档案读取失败'), `ACTUAL_PROFILE_REFRESH_RECOVERS_${width}`)
  }
  check(report.errors.length === 0 && report.blockedMutations.length === 0, 'NO_BROWSER_ERRORS_OR_DEVICE_MUTATIONS')
  report.state = 'PASS'
} catch (error) { report.state = 'FAIL'; report.errors.push(String(error)); process.exitCode = 1 }
finally { save(); await browser.close(); console.log(output) }
