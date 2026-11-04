import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const dll = path.join(repo, 'tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll')
const snapshot = JSON.parse(execFileSync('dotnet', [dll, '--wireless-fixture-json', path.join(repo, 'tests/NetConsole.Next.Service.Tests/fixtures/r3_dual_ac_wireless.json')], { encoding: 'utf8' }))
const output = workspacePath('build', 'autonomous-20261105', 'gui')
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
const errors = []
const evidence = []
let mode = 'normal'
let resourcePageTwo = false
page.on('console', message => { if(message.type() === 'error') console.log('CONSOLE=' + message.text()) })
page.on('requestfailed', request => console.log('FAILED=' + request.url()))
page.on('pageerror', error => errors.push(error.message))
const allResources = Array.from({ length: 105 }, (_, i) => ({ ...snapshot.apResources[0], id: `fixture-ap-${i}`, displayName: `Fixture-AP-${String(i).padStart(3, '0')}`, serialEvidence: `SN-${i}` }))
const base = process.env.NETCONSOLE_NEXT_UI_URL || 'http://127.0.0.1:18792'
function paged(items, url) {
 const current = Number(url.searchParams.get('page') || 1)
 const size = Number(url.searchParams.get('pageSize') || 100)
 return { items: items.slice((current - 1) * size, current * size), page: current, pageSize: size, hasMore: current * size < items.length, total: items.length, warnings: ['OFFLINE_FIXTURE_EVIDENCE'], scope: snapshot.scope, sourceRevision: mode === 'revision-drift' && current > 1 ? 'changed-revision' : 'fixture-revision' }
}
await page.route('**/api/**', async route => {
 const url = new URL(route.request().url())
 const pathname = url.pathname
 if (!pathname.startsWith('/api/')) return route.continue()
 let body = {}
 let status = 200
 if (pathname === '/api/auth/me') body = { username: 'fixture-operator', role: 'SUPERADMIN' }
 else if (pathname === '/api/auth/csrf') body = { csrfToken: 'synthetic-fixture-csrf' }
 else if (pathname === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', resources: {} }
 else if (pathname === '/api/system/devices') body = { items: [], total: 0, summary: {} }
 else if (pathname.endsWith('/wireless/ac-domains')) {
   if (mode === 'denied') { status = 403; body = { error: 'WIRELESS_SCOPE_DENIED' } }
   else body = paged(mode === 'empty' ? [] : snapshot.acDomains, url)
 } else if (pathname.endsWith('/wireless/ap-resources')) {
   if (url.searchParams.get('page') === '2') resourcePageTwo = true
   body = paged(mode === 'empty' ? [] : allResources, url)
 } else if (pathname.endsWith('/ap-groups')) {
   if (mode === 'group-error') { status = 503; body = { error: 'fixture group unavailable' } }
   else body = paged(snapshot.apGroups, url)
 } else { status = 404; body = { error: 'UNEXPECTED_FIXTURE_ROUTE', path: pathname } }
 await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
})
async function capture(name, interaction) {
 const screenshot = path.join(output, name + '.png')
 await page.screenshot({ path: screenshot, fullPage: true })
 evidence.push({ page: '无线业务', route: '/wireless', state: mode, viewport: page.viewportSize(), dpr: await page.evaluate(() => devicePixelRatio), screenshot, interaction, result: 'PASS', evidence: 'OFFLINE_FIXTURE_EVIDENCE' })
}
try {
 await page.goto(base + '/wireless', {waitUntil: 'networkidle'})
 await expect(page.getByText('离线演示数据 · 非模拟器实时状态')).toBeVisible()
 await expect(page.locator('.wireless-table tbody tr')).toHaveCount(20)
 expect(resourcePageTwo).toBe(true)
 await capture('wireless-1920', 'Domain、双 AC、多来源、分页显示')
 await page.setViewportSize({ width: 2560, height: 1440 })
 await capture('wireless-2560', '辅助视口')
 await page.getByLabel('搜索 AP').fill('SN-104')
 await expect(page.locator('.wireless-table tbody tr')).toHaveCount(1)
 await expect(page.locator('.wireless-table')).toContainText('Fixture-AP-104')
 await page.getByLabel('搜索 AP').fill('not-found')
 await expect(page.locator('.wireless-table')).toHaveCount(0)
 await expect(page.getByText('选择一条 AP')).toBeVisible()
 await page.getByLabel('搜索 AP').fill('')
 await page.locator('.wireless-tree__member').first().click()
 await expect(page.locator('.wireless-group-row')).toHaveCount(1)
 await expect(page.locator('.wireless-observation')).toHaveCount(1)
 await capture('wireless-ac1', 'AC 筛选同时限制组与详情')
 await page.locator('.wireless-tree__member').nth(1).click()
 await expect(page.locator('.wireless-observation')).toHaveCount(1)
 await capture('wireless-ac2', 'AC2 独立来源')
 await page.getByLabel('筛选 AP 状态').selectOption('OFFLINE')
 await expect(page.getByText('选择一条 AP')).toBeVisible()
 await page.getByLabel('筛选 AP 状态').selectOption('')
 mode = 'group-error'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('AP 组读取失败')).toBeVisible()
 await capture('wireless-group-error', '采集/API 失败可见，不伪装空组')
 mode = 'denied'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('无线业务暂不可用')).toBeVisible()
 await expect(page.locator('.wireless-table')).toHaveCount(0)
 await capture('wireless-denied', '撤销后不残留旧数据')
 mode = 'revision-drift'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('无线业务暂不可用')).toBeVisible()
 await expect(page.locator('.wireless-table')).toHaveCount(0)
 await capture('wireless-revision-drift', '分页间revision变化时拒绝拼接')
 mode = 'empty'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('暂无无线业务数据')).toBeVisible()
 await capture('wireless-empty', '空态')
 expect(errors).toEqual([])
 fs.writeFileSync(path.join(output, 'evidence.json'), JSON.stringify({ status: 'PASS', evidence, browserErrors: errors, assertions: 'pagination beyond 100, search, source/detail/group isolation, status, errors, empty, fixture label' }, null, 2))
 console.log('WIRELESS_BROWSER_FIXTURE=PASS; screenshots=' + evidence.length)
} catch (error) { console.log('PAGE_ERRORS=' + JSON.stringify(errors)); console.log((await page.locator('body').innerText()).slice(0,2500)); await page.screenshot({path:path.join(output,'failure.png')}); throw error } finally { await browser.close() }
