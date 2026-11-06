// Source Vue UI + Chrome with mocked APIs. This does not connect network devices.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'device-connection-batch-browser')
fs.mkdirSync(output, { recursive: true })
const server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: 0 } })
const errors = []; const unexpected = []; const scenarios = []
let browser; let page
try {
  await server.listen()
  const base = `http://127.0.0.1:${server.httpServer.address().port}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
  page.on('pageerror', error => errors.push(error.message))
  let role = 'SUPERADMIN'; let state = 'RUNNING'; let mode = 'normal'; let releasePost
  const posts = []; const cancellations = []; let reportReads = 0
  const devices = ['a', 'b'].map(id => ({ id: `device-${id}`, deviceId: `device-${id}`, name: `批量交换机${id}`, managementIp: `192.0.2.${id === 'a' ? 10 : 11}`, managementAddress: `192.0.2.${id === 'a' ? 10 : 11}`, vendor: 'H3C', model: 'fixture', deviceType: 'SWITCH', siteId: 'local', site: '测试局点', group: '未分组', enabled: true, terminalProfileId: `profile-${id}`, connectionStatus: 'UNKNOWN', isValid: true, qualityIssues: [] }))
  const profiles = devices.map(device => ({ id: device.terminalProfileId, host: device.managementIp, port: 22, username: 'reader', enabled: true, terminalProtocol: 'SSH', supportedProtocols: ['SSH'] }))
  const plan = () => ({ id: 'connection-plan-fixture', taskType: 'H3C_SSH_READONLY', state, actorUserId: 'fixture-user', commandPlan: ['display version'], claimsRevision: 'GLOBAL', createdMs: 1000, updatedMs: 1000, items: profiles.map((profile, ordinal) => ({ ordinal, target: profile.id, definitionId: `definition-${ordinal}`, runId: `run-${ordinal}`, state: ordinal === 0 && state === 'RUNNING' ? 'SUCCEEDED' : state, error: null })) })
  const report = () => ({ plan: plan(), items: plan().items.map(item => ({ item, run: { id: item.runId, taskType: 'H3C_SSH_READONLY', state: item.state, definitionId: item.definitionId, failureReason: null }, commands: [{ index: 0, command: 'display version', state: item.state, error: null }] })) })
  await page.route(/\/api\/(?:auth|system)\//, async route => {
    const request = route.request(); const endpoint = new URL(request.url()).pathname
    let body; let status = 200
    if (endpoint === '/api/auth/me') body = { username: 'fixture-user', role }
    else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-fixture-csrf' }
    else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', instanceId: 'fixture' }
    else if (endpoint === '/api/system/modules') body = ['devices', 'h3c', 'tasks', 'config-backups'].map(key => ({ key, enabled: mode !== 'disabled' || key !== 'h3c' }))
    else if (endpoint === '/api/system/device-sites') body = [{ id: 'local', name: '测试局点' }, { id: 'other', name: '其他局点' }]
    else if (endpoint === '/api/system/device-groups') body = []
    else if (endpoint === '/api/system/devices') body = { items: devices, total: 2, page: 1, pageSize: 20, hasMore: false, summary: { totalRecords: 2, validDevices: 2, invalidRecords: 0, enabledDevices: 2, disabledDevices: 0, onlineDevices: 0, offlineDevices: 0, unknownStatus: 2 } }
    else if (endpoint === '/api/system/terminal/devices' || endpoint === '/api/system/h3c/profiles') body = profiles
    else if (endpoint === '/api/system/devices/connection-tests' && request.method() === 'POST') {
      posts.push(request.postDataJSON())
      if (mode === 'delayed') await new Promise(resolve => { releasePost = resolve })
      status = 201; body = plan()
    } else if (endpoint === '/api/system/h3c/task-plans/connection-plan-fixture/report') { reportReads++; body = report() }
    else if (endpoint === '/api/system/h3c/task-plans/connection-plan-fixture/cancel') { cancellations.push(endpoint); state = 'CANCELLED'; body = plan() }
    else if (endpoint === '/api/system/h3c/task-plans/connection-plan-fixture') body = plan()
    else { unexpected.push(`${request.method()} ${endpoint}`); status = 404; body = {} }
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  })

  async function selectTargets() {
    await page.getByRole('checkbox', { name: '选择设备 批量交换机a', exact: true }).check()
    await page.getByRole('checkbox', { name: '选择设备 批量交换机b', exact: true }).check()
  }
  await page.goto(`${base}/devices`)
  await selectTargets()
  await page.getByRole('button', { name: '批量测试连接', exact: true }).click()
  const batch = page.getByTestId('device-connection-batch')
  await expect(batch).toBeVisible()
  await expect.poll(() => reportReads).toBeGreaterThan(0)
  expect(posts).toEqual([{ deviceIds: ['device-a', 'device-b'] }])
  await expect(batch).toContainText('profile-a')
  await expect(batch).toContainText('profile-b')
  state = 'SUCCEEDED'
  await batch.getByRole('button', { name: '刷新结果', exact: true }).click()
  await expect(batch.getByRole('button', { name: '取消测试', exact: true })).toHaveCount(0)
  for (const [width, height] of [[1920, 1080], [2560, 1440]]) {
    await page.setViewportSize({ width, height })
    await expect(batch).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: path.join(output, `batch-${width}.png`), fullPage: true })
  }
  scenarios.push('explicit-inventory-ids-create-plan-refresh-complete-1920-and-2560')

  state = 'RUNNING'
  await page.goto(`${base}/devices`); await selectTargets()
  await page.getByRole('button', { name: '批量测试连接', exact: true }).click()
  await expect(batch.getByRole('button', { name: '取消测试', exact: true })).toBeEnabled()
  await batch.getByRole('button', { name: '取消测试', exact: true }).click()
  await expect.poll(() => cancellations.length).toBe(1)
  await expect(batch.getByRole('button', { name: '取消测试', exact: true })).toHaveCount(0)
  scenarios.push('existing-plan-cancellation')

  mode = 'delayed'; state = 'RUNNING'
  await page.goto(`${base}/devices`); await selectTargets()
  await page.getByRole('button', { name: '批量测试连接', exact: true }).click()
  await expect.poll(() => typeof releasePost).toBe('function')
  await page.locator('.scope-site-select select').selectOption('other')
  const delayedResponse = page.waitForResponse(response => response.url().endsWith('/api/system/devices/connection-tests') && response.request().method() === 'POST')
  releasePost()
  await (await delayedResponse).finished()
  await page.waitForLoadState('networkidle')
  await expect(batch).toHaveCount(0)
  await expect(page.getByText('已选择 0 台', { exact: false })).toBeVisible()
  scenarios.push('site-change-discards-delayed-create-response')

  mode = 'normal'; role = 'ADMIN'
  await page.goto(`${base}/devices`)
  await expect(page.getByRole('heading', { name: '设备管理', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '批量测试连接', exact: true })).toHaveCount(0)
  scenarios.push('admin-no-batch-task-write-control')
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  const result = { status: 'PASS', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING', scenarios, errors, unexpected }
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result))
} catch (error) {
  if (page) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: await page.locator('body').innerText() })) }
  throw error
} finally {
  await browser?.close()
  await server.close()
}
