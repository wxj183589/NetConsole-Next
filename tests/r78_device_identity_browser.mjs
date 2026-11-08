// Actual authenticated published UI and scoped inventory detail reads only.
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R78_UI_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', `r78-device-identity-browser-${new Date().toISOString().replace(/[:.]/g, '-')}`)
mkdirSync(output, { recursive: true })
const report = { base, evidenceLevel: 'SERVICE_E2E_BROWSER_READONLY', checks: [], errors: [], mutations: [], identities: [] }
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
  for (const width of [1920, 2560]) {
    await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
    await page.goto(base + '/', { waitUntil: 'networkidle' })
    const link = page.locator('.operational-health tbody a').first()
    await link.waitFor()
    const href = await link.getAttribute('href'), name = await link.innerText()
    const linked = new URL(href, base), id = linked.searchParams.get('deviceId')
    check(linked.pathname === '/devices' && !!id && !linked.searchParams.has('q'), `STABLE_ID_LINK_${width}`)
    const response = page.waitForResponse(value => new URL(value.url()).pathname === '/api/system/devices/' + encodeURIComponent(id) && value.request().method() === 'GET')
    await link.click()
    const detailResponse = await response, detail = await detailResponse.json()
    check(detailResponse.ok() && detail.item.id === id && detail.item.name === name, `EXACT_AUTHORIZED_DETAIL_${width}`)
    const dialog = page.getByRole('dialog', { name: '设备详情 / 审计', exact: true })
    await dialog.waitFor()
    await page.waitForFunction(() => !document.getAnimations().some(animation => animation.playState === 'running'))
    const box = await dialog.boundingBox()
    report.identities.push({ width, id, name, managementIp: detail.item.managementIp, box })
    check(box && box.width > 0 && box.height > 0 && box.x >= 0 && box.x + box.width <= width + 1 && box.y >= 0 && box.y < page.viewportSize().height, `DETAIL_INSIDE_VIEWPORT_${width}`)
    check((await dialog.innerText()).includes(name), `DETAIL_VISIBLE_${width}`)
    check(await page.evaluate(() => document.documentElement.scrollWidth) <= width + 1, `NO_HORIZONTAL_OVERFLOW_${width}`)
    await page.screenshot({ path: path.join(output, `exact-device-${width}.png`), fullPage: true })
    await page.goto(base + '/devices?deviceId=00000000-0000-0000-0000-000000000000', { waitUntil: 'networkidle' })
    await page.getByRole('alert').filter({ hasText: '设备不存在、无权查看或详情读取失败' }).waitFor()
    check(await page.getByRole('dialog', { name: '设备详情 / 审计', exact: true }).count() === 0, `UNKNOWN_ID_NO_SUBSTITUTION_${width}`)
    await page.goto(base + '/devices?deviceId=', { waitUntil: 'networkidle' })
    await page.getByRole('alert').filter({ hasText: '设备链接无效' }).waitFor()
    check(await page.getByRole('dialog', { name: '设备详情 / 审计', exact: true }).count() === 0, `EMPTY_ID_ERROR_${width}`)
    await page.goto(base + '/devices', { waitUntil: 'networkidle' })
    check(await page.getByRole('alert').filter({ hasText: /设备链接无效|设备不存在、无权查看或详情读取失败/ }).count() === 0, `REMOVED_LINK_CLEARS_ERROR_${width}`)
  }
  check(report.errors.length === 0, 'NO_BROWSER_ERRORS')
  check(report.mutations.length === 0, 'NO_DEVICE_MUTATIONS')
  report.state = 'PASS'; save(); console.log(output)
} catch (error) { report.state = 'FAIL'; report.error = String(error); save(); throw error }
finally { await browser.close() }
