// Real authenticated UI with explicit health-error and inventory response fixtures.
// No diagnostics, device connections or device mutations are permitted.
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R78_UI_URL || 'http://127.0.0.1:5174'
const output = workspacePath('reports', `r78-dashboard-inventory-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const report = { base, evidenceLevel: 'BROWSER_UI_EXPLICIT_HEALTH_INVENTORY_FIXTURES', checks: [], errors: [], mutations: [], layouts: [] }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(report, null, 2))
const check = (value, label) => { if (!value) throw new Error(label); report.checks.push(label); save(); console.log(label + '=PASS') }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
try {
  const context = await browser.newContext()
  await context.route('**/api/**', async route => {
    const request = route.request(), pathname = new URL(request.url()).pathname
    if (['GET', 'HEAD', 'OPTIONS'].includes(request.method()) || pathname === '/api/auth/login') return route.continue()
    report.mutations.push({ pathname, method: request.method() }); await route.abort('blockedbyclient')
  })
  const page = await context.newPage(); page.setDefaultTimeout(20000)
  page.on('pageerror', error => report.errors.push(error.message))
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  let healthFails = true
  await page.route('**/api/system/health', route => healthFails
    ? route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"browser health failure fixture"}' }) : route.continue())
  const inventory = Array.from({ length: 101 }, (_, index) => ({
    id: `browser-device-${index + 1}`, name: `浏览器设备${String(index + 1).padStart(3, '0')}`,
    managementIp: `192.0.2.${index + 1}`, enabled: true, isValid: true,
  }))
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    healthFails = true
    await page.goto(base + '/', { waitUntil: 'networkidle' })
    await page.getByText('告警状态未知', { exact: true }).waitFor()
    check((await page.locator('.dashboard-alert-table').innerText()).includes('告警状态未知'), `HEALTH_FAILURE_UNKNOWN_${width}`)
    check(!(await page.locator('.dashboard-alert-table').innerText()).includes('当前实例没有告警'), `NO_FALSE_HEALTHY_${width}`)
    check((await page.locator('.overview-footer').last().innerText()).includes('Raw 写入：未知'), `RAW_UNKNOWN_${width}`)
    await page.screenshot({ path: path.join(output, `health-unknown-${width}.png`), fullPage: true })
    healthFails = false
    const recoveredHealth = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/health' && response.status() === 200)
    await page.getByRole('button', { name: '刷新数据', exact: true }).click()
    await recoveredHealth
    await page.getByText('告警状态未知', { exact: true }).waitFor({ state: 'hidden' })
    check(!(await page.locator('.dashboard-alert-table').innerText()).includes('告警状态未知'), `ACTUAL_HEALTH_RECOVERY_${width}`)
    await page.route('**/api/system/devices?**', route => {
      const url = new URL(route.request().url()), query = url.searchParams.get('search') || ''
      const filtered = inventory.filter(item => item.name.includes(query) || item.managementIp.includes(query))
      const current = Number(url.searchParams.get('page') || 1), size = Number(url.searchParams.get('pageSize') || 100)
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        page: current, pageSize: size, total: filtered.length, hasMore: current * size < filtered.length,
        items: filtered.slice((current - 1) * size, current * size), summary: {},
      }) })
    })
    await page.goto(base + '/tools', { waitUntil: 'networkidle' })
    const select = page.getByRole('listbox', { name: '诊断设备', exact: true })
    await select.selectOption(['browser-device-1'])
    await page.getByRole('button', { name: '下一页设备', exact: true }).click()
    await page.getByRole('status', { name: '诊断设备分页', exact: true }).filter({ hasText: '第 2 页' }).waitFor()
    check(await select.locator('option').count() === 2, `CROSS_PAGE_SELECTION_VISIBLE_${width}`)
    await select.selectOption(['browser-device-1', 'browser-device-101'])
    check((await page.getByRole('status', { name: '诊断设备分页', exact: true }).innerText()).includes('已选 2 台'), `DEVICE_101_SELECTABLE_${width}`)
    await page.getByRole('textbox', { name: '搜索诊断设备', exact: true }).fill('不存在的设备')
    await page.getByRole('button', { name: '搜索设备', exact: true }).click()
    await page.getByRole('status', { name: '诊断设备分页', exact: true }).filter({ hasText: '匹配 0 条' }).waitFor()
    check(await select.locator('option').count() === 2, `SEARCH_RETAINS_SELECTION_${width}`)
    await page.getByRole('button', { name: '清空设备选择', exact: true }).click()
    check(await select.locator('option').count() === 0, `CLEAR_CROSS_PAGE_SELECTION_${width}`)
    const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth)
    report.layouts.push({ width, scrollWidth })
    check(scrollWidth <= width + 1, `NO_HORIZONTAL_OVERFLOW_${width}`)
    await page.screenshot({ path: path.join(output, `inventory-pagination-${width}.png`), fullPage: true })
    await page.unroute('**/api/system/devices?**')
  }
  check(report.errors.length === 0, 'NO_BROWSER_ERRORS')
  check(report.mutations.length === 0, 'NO_DIAGNOSTICS_OR_DEVICE_MUTATIONS')
  report.state = 'PASS'; save(); console.log(output)
} catch (error) { report.state = 'FAIL'; report.error = String(error); save(); throw error }
finally { await browser.close() }
