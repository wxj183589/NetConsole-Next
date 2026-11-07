import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

// Actual Vue -> authenticated service -> authorized simulator calls. No mocks,
// shell login automation, device configuration writes or manufactured faults.
const ui = process.env.R70_UI_URL || 'http://127.0.0.1:5174'
const api = process.env.R70_API_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', process.env.R70_BROWSER_OUTPUT || 'r70-browser-20261107')
mkdirSync(output, { recursive: true })
const result = { evidence: 'SIMULATOR', ui, api, startedAt: new Date().toISOString(), checks: {}, apiFailures: [], pageErrors: [], consoleErrors: [], layouts: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
page.setDefaultTimeout(20000)
let authenticated = false
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
page.on('pageerror', error => result.pageErrors.push(error.message))
page.on('console', message => { if (message.type() === 'error' && (authenticated || !message.text().includes('401'))) result.consoleErrors.push(message.text()) })
page.on('response', response => {
  const pathname = new URL(response.url()).pathname
  if (pathname.startsWith('/api/') && response.status() >= 400 &&
    !(pathname === '/api/auth/me' && !authenticated) &&
    !(/^\/api\/system\/sites\/[^/]+\/ssh-jump-host$/.test(pathname) && response.status() === 404))
    result.apiFailures.push(`${response.status()} ${pathname}`)
})
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const requestUrl = new URL(route.request().url())
  const headers = { ...route.request().headers() }
  if (headers.origin) headers.origin = api
  try { await route.fulfill({ response: await route.fetch({ url: `${api}${requestUrl.pathname}${requestUrl.search}`, headers, timeout: 240000 }) }) }
  catch (error) {
    result.apiFailures.push(`TRANSPORT ${requestUrl.pathname}: ${String(error).split('\n')[0]}`)
    await route.abort('failed').catch(() => {})
  }
})
const assert = (condition, reason) => { if (!condition) throw new Error(reason) }
const read = async url => page.evaluate(async target => {
  const response = await fetch(target, { credentials: 'same-origin' })
  if (!response.ok) throw new Error(`${response.status} ${target}`)
  return response.json()
}, url)
const navigate = async name => {
  await page.locator('.app-sidebar').getByRole('button', { name, exact: true }).click()
  await page.getByRole('heading', { name, exact: true }).waitFor()
}
const screenshots = async name => {
  for (const viewport of [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
    await page.setViewportSize(viewport)
    await page.screenshot({ path: path.join(output, `${name}-${viewport.width}.png`), fullPage: true })
    const layout = await page.evaluate(() => ({ viewport: innerWidth, document: document.documentElement.scrollWidth }))
    result.layouts.push({ name, ...layout })
    assert(layout.document <= layout.viewport + 1, `${name}: document has horizontal overflow ${layout.document}/${layout.viewport}`)
  }
}
const step = async (name, action) => {
  if (name !== 'login' && process.env.R70_STEPS && !process.env.R70_STEPS.split(',').includes(name)) return
  console.log(`Starting ${name}`)
  try { result.checks[name] = { status: 'PASS', ...await action() } }
  catch (error) {
    result.checks[name] = { status: 'FAIL', error: String(error) }
    await page.screenshot({ path: path.join(output, `${name}-failure.png`), fullPage: true }).catch(() => {})
  }
  save()
  console.log(`${name}: ${result.checks[name].status}`)
}
try {
  await step('login', async () => {
    await page.goto(ui, { waitUntil: 'networkidle' })
    await page.getByLabel('用户名').fill(process.env.R70_USER || 'superadmin')
    await page.getByLabel('密码').fill(process.env.R70_PASSWORD || 'superadmin')
    await page.getByRole('button', { name: '登录工作台' }).click()
    await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
    authenticated = true
    return {}
  })
  if (!authenticated) throw new Error('Login failed; business operations skipped')

  await step('quickPing', async () => {
    await navigate('工具集')
    const devices = page.getByLabel('诊断设备', { exact: true })
    const target = process.env.R70_AC1_ID || 'r68-ac1'
    await devices.locator(`option[value="${target}"]`).waitFor({ state: 'attached' })
    await devices.selectOption([target])
    const controls = page.locator('.diagnostic-controls')
    for (const checkbox of await controls.locator('input[type="checkbox"]').all()) await checkbox.uncheck()
    await controls.locator('input[value="QUICK_PING"]').check()
    await page.getByLabel('Quick Ping 协议').selectOption('TCP')
    await page.getByLabel('Quick Ping 采样次数').fill('3')
    await page.getByLabel('Quick Ping 采样间隔', { exact: true }).fill('100')
    const created = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/diagnostics/runs' && response.request().method() === 'POST')
    await page.getByRole('button', { name: '开始诊断', exact: true }).click()
    const response = await created
    const start = await response.json()
    assert(response.ok(), 'Quick Ping admission failed')
    let completed
    for (let count = 0; count < 45; count++) {
      completed = await read(`/api/system/diagnostics/runs/${encodeURIComponent(start.id)}`)
      if (completed.progressPercent === 100) break
      await page.waitForTimeout(1000)
    }
    const item = completed.items.find(item => item.deviceId === target && item.kind === 'QUICK_PING')
    assert(item?.quickPing?.sent === 3 && item.quickPing.samples.length === 3, 'Missing actual three-sample diagnostic evidence')
    assert(item.quickPing.protocol === 'TCP', 'Quick Ping protocol mismatch')
    await page.locator('.quick-ping-samples span').nth(2).waitFor()
    await screenshots('quick-ping')
    return { runId: start.id, item }
  })

  await step('operationalHealth', async () => {
    await navigate('运行总览')
    const panel = page.locator('.operational-health')
    await panel.getByRole('heading', { name: '设备健康与故障事件' }).waitFor()
    await panel.locator('tbody tr').first().waitFor()
    const health = await read('/api/system/operations/health?page=1')
    assert(health.devices.length > 0, 'No scoped health devices')
    assert(health.devices.every(device => device.cpuPercent.state === 'UNKNOWN' && device.memoryPercent.state === 'UNKNOWN'), 'Unsupported CPU/memory must stay UNKNOWN')
    assert((await panel.innerText()).includes('采集失败不代表设备离线'), 'Missing evidence boundary')
    if (result.checks.quickPing?.status === 'PASS') {
      const target = health.devices.find(device => device.deviceId === (process.env.R70_AC1_ID || 'r68-ac1'))
      assert(target?.packetLossPercent?.state === 'OBSERVED' && target.pingDelayMs?.value !== null,
        'Quick Ping result not projected into health')
    }
    await screenshots('operational-health')
    return { health }
  })

  await step('readOnlyRecovery', async () => {
    await navigate('运行总览')
    const panel = page.locator('.operational-health')
    await panel.locator('tbody tr').first().waitFor()
    await panel.getByLabel('恢复目标').selectOption(process.env.R70_AC1_ID || 'r68-ac1')
    const responsePromise = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/operations/recover' && response.request().method() === 'POST', { timeout: 180000 })
    await panel.getByRole('button', { name: '执行只读恢复', exact: true }).click()
    const response = await responsePromise
    const recovery = await response.json()
    assert(response.ok() && recovery.state === 'RECOLLECTION_QUEUED', `Recovery did not queue recollection: ${recovery.state || response.status()}`)
    assert(recovery.attempts.length >= 1 && recovery.attempts.length <= 3, 'Recovery attempt budget violation')
    let report
    for (let count = 0; count < 90; count++) {
      report = await read(`/api/system/h3c/task-plans/${encodeURIComponent(recovery.planId)}/report`)
      if (report.items.every(item => item.run && ['SUCCEEDED', 'FAILED', 'CANCELLED', 'TIMED_OUT'].includes(item.run.state))) break
      await page.waitForTimeout(2000)
    }
    assert(report.items.every(item => item.run?.state === 'SUCCEEDED' && item.commands.length === 2 && item.commands.every(command => command.state === 'SUCCEEDED')), 'Recollection did not complete successfully')
    await panel.getByRole('button', { name: '读取补采结果', exact: true }).click()
    await panel.locator('[aria-label="恢复补采结果"]').getByText('display version', { exact: false }).waitFor()
    assert((await panel.innerText()).includes('不代表业务恢复完成'), 'Recovery UI overclaims login success')
    await screenshots('read-only-recovery')
    return { recovery, report, limitation: 'Healthy simulator connection plus actual recollection validated; no induced device outage or false business recovery claim.' }
  })

  await step('deviceCapabilities', async () => {
    await navigate('设备管理')
    await page.getByLabel('授权局点').selectOption('')
    const row = page.locator('[data-testid="device-inventory-table"] tbody tr').filter({ hasText: process.env.R70_AC1_ID || 'r68-ac1' })
    await row.getByRole('button', { name: '详情', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '设备详情 / 审计' })
    await dialog.waitFor()
    const capabilities = dialog.locator('[data-testid="device-operational-capabilities"]')
    await capabilities.locator('tbody tr').first().waitFor()
    assert(await capabilities.locator('[role="alert"]').count() === 0, 'Actual device capability API failed')
    const capabilityText = await capabilities.innerText()
    const detail = await dialog.innerText()
    assert(!detail.includes('当前版本没有对应设备详情 API'), 'Stale capability placeholder remains')
    await capabilities.scrollIntoViewIfNeeded()
    await screenshots('device-capabilities')
    await dialog.getByRole('button', { name: 'Close this dialog' }).click()
    await page.getByRole('button', { name: '高级设备资料', exact: true }).click()
    const editor = page.getByRole('dialog', { name: '新增设备', exact: true })
    const typeSelect = editor.locator('label').filter({ has: page.locator('span', { hasText: /^设备类型$/ }) }).locator('select')
    await typeSelect.locator('option').first().waitFor({ state: 'attached' })
    const types = await typeSelect.locator('option').evaluateAll(options => options.map(option => option.value))
    assert(types.length > 0, 'Device editor type options not loaded')
    assert(!types.includes('FIT_AP'), 'FIT-AP independent inventory creation remains offered')
    await editor.getByRole('button', { name: '取消', exact: true }).click()
    return { detail, capabilities: capabilityText, createTypes: types }
  })

  await step('completeConfiguration', async () => {
    await page.goto(`${ui}/config-backups?deviceId=${encodeURIComponent(process.env.R70_AC1_ID || 'r68-ac1')}`, { waitUntil: 'networkidle' })
    const panel = page.locator('section[aria-label="模拟器完整配置"]')
    await panel.getByRole('button', { name: '刷新完整备份', exact: true }).click()
    const baseline = panel.getByLabel('完整配置基线')
    await page.waitForFunction(() => !document.querySelector('[aria-label="完整配置基线"]')?.disabled)
    const ids = await baseline.locator('option').evaluateAll(options => options.filter(option => option.value && !option.disabled).map(option => option.value))
    assert(ids.length > 0, 'No verified complete configuration backup')
    await baseline.selectOption(ids[0])
    await panel.getByRole('button', { name: '查看脱敏完整配置', exact: true }).click()
    await panel.locator('pre').first().waitFor()
    const text = await panel.locator('pre').first().innerText()
    assert(text.length > 100, 'Complete configuration view is empty')
    await panel.getByLabel('比较源备份').selectOption(ids[0])
    await panel.getByLabel('比较目标备份').selectOption(ids[1] || ids[0])
    await panel.getByRole('button', { name: '比较完整配置', exact: true }).click()
    await panel.locator('[aria-label="配置比较结果"]').waitFor()
    await screenshots('complete-configuration')
    return { verifiedBackupCount: ids.length, comparison: await panel.locator('[aria-label="配置比较结果"]').innerText(), previewOnly: true }
  })

  await step('wirelessAndTopology', async () => {
    await navigate('无线业务')
    await page.getByRole('heading', { name: '无线任务历史' }).waitFor()
    await page.locator('.wireless-tree__domain').first().waitFor({ timeout: 60000 })
    await page.locator('.wireless-table tbody tr').first().waitFor({ timeout: 60000 })
    await page.locator('.wireless-layout > .el-loading-mask').waitFor({ state: 'hidden', timeout: 60000 })
    await page.waitForLoadState('networkidle')
    await screenshots('wireless')
    const wirelessText = (await page.locator('.app-main').innerText()).slice(0, 16000)
    await navigate('拓扑与资产')
    await page.waitForLoadState('networkidle')
    await screenshots('topology')
    return { wirelessText, topologyText: (await page.locator('.app-main').innerText()).slice(0, 12000) }
  })
  result.overall = Object.values(result.checks).some(check => check.status === 'FAIL') || result.pageErrors.length || result.apiFailures.length || result.consoleErrors.length ? 'PARTIAL' : 'PASS_WITH_LIMITATIONS'
} catch (error) { result.overall = 'BLOCKED'; result.error = String(error) }
finally { result.completedAt = new Date().toISOString(); save(); await page.unrouteAll({ behavior: 'ignoreErrors' }); await browser.close() }
console.log(JSON.stringify({ output, overall: result.overall, checks: Object.fromEntries(Object.entries(result.checks).map(([key, value]) => [key, value.status])), apiFailures: result.apiFailures, pageErrors: result.pageErrors, consoleErrors: result.consoleErrors }, null, 2))
if (result.overall !== 'PASS_WITH_LIMITATIONS') process.exitCode = 1
