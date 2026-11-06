import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const dll = path.join(repo, 'tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll')
const snapshot = JSON.parse(execFileSync('dotnet', [dll, '--wireless-fixture-json', path.join(repo, 'tests/NetConsole.Next.Service.Tests/fixtures/r3_dual_ac_wireless.json')], { encoding: 'utf8' }))
const runtimeSnapshot = JSON.parse(execFileSync('dotnet', [dll, '--wireless-runtime-fixture-json', path.join(repo, 'tests/NetConsole.Next.Service.Tests/fixtures/r3_runtime_state_history.json')], { encoding: 'utf8' }))
const sourceRuntime = Object.fromEntries(snapshot.acDomains.flatMap(domain => domain.memberDeviceIds).map(source => [source,
 JSON.parse(execFileSync('dotnet', [dll, '--wireless-runtime-fixture-json', path.join(repo, 'tests/NetConsole.Next.Service.Tests/fixtures/r3_runtime_state_history.json'), source], { encoding: 'utf8' }))]))
const output = workspacePath('build', 'autonomous-20261105', 'gui')
fs.mkdirSync(output, { recursive: true })
const browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
const errors = []
const unexpectedRequests = []
const cancelledRequests = []
const unexpectedRoutes = []
const evidence = []
let mode = 'normal'
let currentRole = 'SUPERADMIN'
let fileRootRequests = 0
let deferNextFileList = false
let releaseFileList
let resourcePageTwo = false
page.on('console', message => { if(message.type() === 'error') console.log('CONSOLE=' + message.text()) })
page.on('requestfailed', request => {
 const failure = { path: new URL(request.url()).pathname, reason: request.failure()?.errorText }
 if (failure.reason === 'net::ERR_ABORTED') cancelledRequests.push(failure)
 else unexpectedRequests.push(failure)
})
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
 if (pathname === '/api/auth/me') body = { username: 'fixture-operator', role: currentRole }
 else if (pathname === '/api/auth/csrf') body = { csrfToken: 'synthetic-fixture-csrf' }
 else if (pathname === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', resources: {} }
 else if (pathname === '/api/system/modules') body = [
   { key: 'devices', label: '设备管理', enabled: true, isCore: true, updatedMs: 1000, updatedBy: 'fixture' },
   { key: 'wireless', label: '无线业务', enabled: true, isCore: false, updatedMs: 1000, updatedBy: 'fixture' },
   { key: 'files', label: '文件管理', enabled: true, isCore: false, updatedMs: 1000, updatedBy: 'fixture' },
 ]
 else if (pathname === '/api/system/devices') body = { items: [], total: 0, summary: {} }
 else if (pathname === '/api/system/files/roots') {
   fileRootRequests++
   if (mode === 'file-denied') { status = 403; body = { error: 'GLOBAL_FILE_SCOPE_REQUIRED' } }
   else if (mode === 'file-session-expired') { status = 401; body = { error: 'UNAUTHORIZED' } }
   else body = [{ id: 'raw', label: 'Raw 采集', exists: true, relativePath: 'raw' }]
 } else if (pathname === '/api/system/files/list') {
   if (mode === 'file-denied') { status = 403; body = { error: 'GLOBAL_FILE_SCOPE_REQUIRED' } }
   else if (mode === 'file-session-expired') { status = 401; body = { error: 'UNAUTHORIZED' } }
   else {
     if (deferNextFileList) { deferNextFileList = false; await new Promise(resolve => { releaseFileList = resolve }) }
     body = { items: [{ name: 'fixture-private.ndjson', relativePath: 'fixture-private.ndjson', kind: 'FILE', size: 128, modifiedMs: 1000, canDownload: true, canArchive: true }], total: 1, hasMore: false, relativePath: '' }
   }
 } else if (pathname.endsWith('/wireless/ac-domains')) {
   if (mode === 'denied') { status = 403; body = { error: 'WIRELESS_SCOPE_DENIED' } }
   else body = paged(mode === 'empty' ? [] : snapshot.acDomains, url)
 } else if (pathname.endsWith('/wireless/ap-resources')) {
   if (url.searchParams.get('page') === '2') resourcePageTwo = true
   body = paged(mode === 'empty' ? [] : allResources, url)
 } else if (pathname.endsWith('/runtime-state')) {
   if (mode === 'runtime-error') { status = 503; body = { error: 'fixture runtime unavailable' } }
   else {
     const source = url.searchParams.get('sourceAcId')
     body = structuredClone(source ? sourceRuntime[source] : runtimeSnapshot)
     body.resourceId = pathname.split('/').at(-2)
     if (mode === 'partial-history') {
       body.sources.forEach(item => { item.historyStatus = 'PARTIAL_SCAN_LIMIT' })
       body.warnings.push('RUNTIME_HISTORY_INCOMPLETE')
     }
   }
 } else if (pathname.includes('/wireless/ap-groups/') && !pathname.endsWith('/members')) {
   const groupId = decodeURIComponent(pathname.split('/').at(-1))
   const group = snapshot.apGroups.find(item => item.id === groupId)
   if (!group) { status = 404; body = { error: 'WIRELESS_RESOURCE_NOT_VISIBLE' } }
   else {
     const observationIds = new Set(group.sourceObservationIds)
     const members = snapshot.apResources.filter(resource => group.memberResourceIds.includes(resource.id))
       .map(resource => ({ ...resource, observations: resource.observations.filter(item => observationIds.has(item.id)) }))
     const observations = members.flatMap(resource => resource.observations)
     body = { group, members, observations, total: members.length, asOf: snapshot.asOf, scope: snapshot.scope, sourceRevision: 'fixture-revision', warnings: ['OFFLINE_FIXTURE_EVIDENCE'], provenance: { sourceAcDeviceId: group.sourceAcDeviceId, sourceAcDomainId: group.sourceAcDomainId, sourceRevision: group.sourceRevision, scopeStatus: group.scopeStatus, groupScope: group.groupScope, sourceGroupKey: group.sourceGroupKey, groupIdentityKey: group.groupIdentityKey, groupIdentityStatus: group.groupIdentityStatus, membershipRevision: group.membershipRevision, sourceObservationIds: group.sourceObservationIds, sourceAcDeviceIds: group.sourceAcDeviceIds, sourceEvidenceRefs: group.sourceEvidenceRefs, observedAtMs: group.observedAtMs, conflictReason: group.conflictReason } }
   }
 } else if (pathname.includes('/wireless/collection-manifests/') && !pathname.endsWith('/collection-manifests')) {
   const id = decodeURIComponent(pathname.split('/').at(-1))
   const items = snapshot.apResources.flatMap(resource => resource.observations).map(item => ({ observationId: item.id, sourceAcDeviceId: item.sourceAcDeviceId, sourceAcDomainId: item.sourceAcDomainId, sourceSiteId: item.sourceSiteId, rawManifestRef: item.rawManifestRef, sourceRevision: item.sourceRevision, capturedMs: item.capturedMs, outcome: 'OBSERVED' }))
   if (id !== `wireless-collection:${snapshot.sourceRevision}`) { status = 404; body = { error: 'WIRELESS_RESOURCE_NOT_VISIBLE' } }
   else body = { manifest: { id, sourceRevision: snapshot.sourceRevision, scopeStatus: snapshot.scope.status, sourceAcDeviceIds: [...new Set(items.map(item => item.sourceAcDeviceId))], firstCapturedMs: Math.min(...items.map(item => item.capturedMs)), lastCapturedMs: Math.max(...items.map(item => item.capturedMs)), observationCount: items.length, status: 'PUBLISHED', createdMs: snapshot.asOf }, items, asOf: snapshot.asOf, scope: snapshot.scope, sourceRevision: snapshot.sourceRevision, warnings: ['OFFLINE_FIXTURE_EVIDENCE'] }
 } else if (pathname.endsWith('/collection-manifests')) {
   const allItems = snapshot.apResources.flatMap(resource => resource.observations)
   const sourceAcId = url.searchParams.get('sourceAcId')
   const items = sourceAcId ? allItems.filter(item => item.sourceAcDeviceId === sourceAcId) : allItems
   const id = `wireless-collection:${snapshot.sourceRevision}`
   body = paged(items.length ? [{ id, sourceRevision: snapshot.sourceRevision, scopeStatus: snapshot.scope.status, sourceAcDeviceIds: [...new Set(items.map(item => item.sourceAcDeviceId))], firstCapturedMs: Math.min(...items.map(item => item.capturedMs)), lastCapturedMs: Math.max(...items.map(item => item.capturedMs)), observationCount: items.length, status: 'PUBLISHED', createdMs: snapshot.asOf }] : [], url)
 } else if (pathname.endsWith('/ap-groups')) {
   if (mode === 'group-error') { status = 503; body = { error: 'fixture group unavailable' } }
   else {
     const source = url.searchParams.get('sourceAcId')
     body = paged(source ? snapshot.apGroups.filter(group => group.sourceAcDeviceIds.includes(source)) : snapshot.apGroups, url)
   }
 } else { status = 404; body = { error: 'UNEXPECTED_FIXTURE_ROUTE', path: pathname }; unexpectedRoutes.push(pathname) }
 await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
})
async function capture(name, interaction) {
 const screenshot = path.join(output, name + '.png')
 await page.screenshot({ path: screenshot, fullPage: true })
 let detailScreenshot
 if (name === 'wireless-1920' || name === 'wireless-2560') {
   detailScreenshot = path.join(output, name + '-state-detail.png')
   await page.locator('.wireless-source-history').first().screenshot({ path: detailScreenshot })
 }
 const route = new URL(page.url()).pathname
 evidence.push({ page: route === '/files' ? '文件管理' : '无线业务', route, state: mode, viewport: page.viewportSize(), dpr: await page.evaluate(() => devicePixelRatio), screenshot, detailScreenshot, interaction, result: 'PASS', evidence: 'OFFLINE_FIXTURE_EVIDENCE' })
}
try {
 await page.goto(base + '/wireless', {waitUntil: 'networkidle'})
 await expect(page.getByText('离线演示数据 · 非模拟器实时状态')).toBeVisible()
 await expect(page.locator('.wireless-table tbody tr')).toHaveCount(20)
 expect(resourcePageTwo).toBe(true)
 await expect(page.locator('.wireless-source-history')).toHaveCount(2)
 await expect(page.locator('.wireless-ap-state-values')).toContainText('0 / 10')
 await expect(page.locator('.wireless-history-table tbody tr')).toHaveCount(20)
 await expect(page.getByText('以下为离线状态历史，不能证明设备当前在线')).toBeVisible()
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
 await page.locator('.wireless-group-row').first().click()
 await expect(page.locator('.wireless-group-detail')).toContainText('来源 AC')
 await expect(page.locator('.wireless-group-detail')).toContainText('待确认')
 await expect(page.locator('.wireless-group-detail')).not.toContainText('AC|ac-device-1|default-group')
 await page.locator('.wireless-collection-row').first().click()
 await expect(page.locator('.wireless-collection-detail')).toContainText('fixture:ac1:')
 await expect(page.locator('.wireless-observation')).toHaveCount(1)
 await expect(page.locator('.wireless-source-history')).toHaveCount(1)
 await expect(page.locator('.wireless-source-history')).toContainText('ac-device-1')
 await expect(page.locator('.wireless-ap-history tbody tr')).toHaveCount(10)
 await capture('wireless-ac1', 'AC 筛选同时限制组与详情')
 await page.locator('.wireless-tree__member').nth(1).click()
 await expect(page.locator('.wireless-observation')).toHaveCount(1)
 await expect(page.locator('.wireless-source-history')).toHaveCount(1)
 await expect(page.locator('.wireless-source-history')).toContainText('ac-device-2')
 await capture('wireless-ac2', 'AC2 独立来源')
 await page.getByLabel('筛选 AP 状态').selectOption('OFFLINE')
 await expect(page.getByText('选择一条 AP')).toBeVisible()
 await page.getByLabel('筛选 AP 状态').selectOption('')
 mode = 'conflict'
 allResources[0].stateConflictReason = 'MULTI_SOURCE_MASTER'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('来源状态存在冲突，请核对各 AC 的原始观察')).toBeVisible()
 await capture('wireless-conflict', '多来源状态冲突提示')
 delete allResources[0].stateConflictReason
 mode = 'runtime-error'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('状态历史读取失败：fixture runtime unavailable')).toBeVisible()
 await expect(page.locator('.wireless-source-history')).toHaveCount(0)
 await capture('wireless-runtime-error', '历史失败不保留旧来源状态')
 mode = 'partial-history'
 await page.getByRole('button', { name: '刷新数据' }).click()
 await expect(page.getByText('历史读取不完整，以下记录可能不足最近十次有效变化')).toBeVisible()
 await capture('wireless-partial-history', '扫描上限或缺证据不虚报完整Recent10')
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
 await page.setViewportSize({ width: 1920, height: 1080 })
 const beforeRoots = fileRootRequests
 mode = 'file-scoped-user'
 currentRole = 'ADMIN'
 await page.goto(base + '/files')
 await page.reload({ waitUntil: 'networkidle' })
 await expect(page.getByText('文件管理需要全局管理员权限')).toBeVisible()
 expect(fileRootRequests).toBe(beforeRoots)
 await expect(page.locator('.root-item')).toHaveCount(0)
 await capture('files-scoped-user', 'Site用户不请求或显示跨Site文件元数据')
 currentRole = 'SUPERADMIN'
 mode = 'normal'
 await page.reload({ waitUntil: 'networkidle' })
 await expect(page.getByText('fixture-private.ndjson')).toBeVisible()
 await capture('files-global-user', '当前全局管理员保留逻辑目录与列表')
 deferNextFileList = true
 await page.getByRole('button', { name: '刷新', exact: true }).click()
 await expect.poll(() => typeof releaseFileList).toBe('function')
 mode = 'file-denied'
 await page.getByRole('button', { name: '刷新', exact: true }).click()
 await expect(page.getByText('文件管理需要全局管理员权限')).toBeVisible()
 await expect(page.getByText('fixture-private.ndjson')).toHaveCount(0)
 await expect(page.locator('.root-item')).toHaveCount(0)
 const lateResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/files/list' && response.status() === 200)
 releaseFileList()
 await (await lateResponse).finished()
 await page.waitForTimeout(100)
 await expect(page.getByText('fixture-private.ndjson')).toHaveCount(0)
 expect(await page.evaluate(() => sessionStorage.getItem('netconsole-files-root'))).toBeNull()
 await capture('files-role-revoked', '403后清空旧目录、文件和任务状态')
 mode = 'normal'
 await page.reload({ waitUntil: 'networkidle' })
 await expect(page.getByText('fixture-private.ndjson')).toBeVisible()
 mode = 'file-session-expired'
 await page.getByRole('button', { name: '刷新', exact: true }).click()
 await expect(page.getByText('登录已失效', { exact: true })).toBeVisible()
 await expect(page.getByText('fixture-private.ndjson')).toHaveCount(0)
 await expect(page.locator('.root-item')).toHaveCount(0)
 await capture('files-session-expired', '401后清除旧元数据和本地认证状态')
 expect(errors).toEqual([])
 expect(unexpectedRequests).toEqual([])
 expect(unexpectedRoutes).toEqual([])
 fs.writeFileSync(path.join(output, 'evidence.json'), JSON.stringify({ status: 'PASS', evidence, browserErrors: errors, unexpectedRequests, cancelledRequests, unexpectedRoutes, assertions: 'pagination beyond 100, search, source/detail/group isolation, status, errors, empty, fixture label' }, null, 2))
 console.log('BROWSER_FIXTURE=PASS; states=' + evidence.length + '; wireless=11; global_files=4')
} catch (error) { console.log('PAGE_ERRORS=' + JSON.stringify(errors)); console.log((await page.locator('body').innerText()).slice(0,2500)); await page.screenshot({path:path.join(output,'failure.png')}); throw error } finally { await browser.close() }
