import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

// Uses the real local API through a transparent browser proxy. No mocked data.
// Business actions are UI clicks; only read-only simulator commands are allowed.
const ui = process.env.R69_UI_URL || 'http://127.0.0.1:5174'
const api = process.env.R69_API_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', process.env.R69_BROWSER_OUTPUT || 'r69-browser-full-chain-20261107')
mkdirSync(output, { recursive: true })
const result = { evidence: 'SIMULATOR', ui, api, startedAt: new Date().toISOString(), checks: {}, apiFailures: [], pageErrors: [], consoleErrors: [], wirelessProgress: [], backupProgress: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
page.setDefaultTimeout(20000)
let authenticated = false
let linkedDevice
let activeTaskId
const responseReads = new Set()
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
page.on('pageerror', error => result.pageErrors.push(error.message))
page.on('console', message => { if (message.type() === 'error' && (authenticated || !message.text().includes('401'))) result.consoleErrors.push(message.text()) })
page.on('response', response => {
  const pathname = new URL(response.url()).pathname
  if (!pathname.startsWith('/api/')) return
  if (response.status() >= 400 && !(pathname === '/api/auth/me' && !authenticated) && !(/^\/api\/system\/sites\/[^/]+\/ssh-jump-host$/.test(pathname) && response.status() === 404)) result.apiFailures.push(`${response.status()} ${pathname}`)
  if (response.ok() && (/^\/api\/system\/wireless\/tasks\/[^/]+$/.test(pathname) || /^\/api\/system\/config-backups\/batches\/[^/]+$/.test(pathname))) {
    const read = response.json().then(body => {
      if (body.summary && (!activeTaskId || body.summary.id === activeTaskId)) result.wirelessProgress.push({ ...body.summary, observedAt: new Date().toISOString(), commands: body.items.map(item => ({ deviceId: item.deviceId, state: item.state, commands: item.commands })) })
      else if (body.items && body.configurationKind) result.backupProgress.push({ id: body.id, state: body.state, total: body.total, succeeded: body.succeeded, items: body.items.map(item => ({ profileId: item.profileId, state: item.state, error: item.error, snapshotId: item.snapshotId })) })
    }).catch(() => {}).finally(() => responseReads.delete(read))
    responseReads.add(read)
  }
})
await page.route(url => url.pathname.startsWith('/api/'), async route => {
  const requestUrl = new URL(route.request().url())
  const headers = { ...route.request().headers() }
  if (headers.origin) headers.origin = api
  await route.fulfill({ response: await route.fetch({ url: `${api}${requestUrl.pathname}${requestUrl.search}`, headers, timeout: 240000 }) })
})
const navigate = async name => {
  await page.locator('.app-sidebar').getByRole('button', { name, exact: true }).click()
  await page.getByRole('heading', { name, exact: true }).waitFor()
}
const screenshots = async name => {
  for (const viewport of [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
    await page.setViewportSize(viewport)
    await page.screenshot({ path: path.join(output, `${name}-${viewport.width}.png`), fullPage: true })
  }
}
const step = async (name, action) => {
  if (name !== 'login' && process.env.R69_STEPS && !process.env.R69_STEPS.split(',').includes(name)) return
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
    await page.getByLabel('用户名').fill(process.env.R69_USER || 'superadmin')
    await page.getByLabel('密码').fill(process.env.R69_PASSWORD || 'superadmin')
    await page.getByRole('button', { name: '登录工作台' }).click()
    await page.getByRole('heading', { name: '运行总览' }).waitFor()
    authenticated = true
    await screenshots('dashboard')
    return {}
  })
  if (!authenticated) throw new Error('Login failed; business operations skipped')

  await step('deviceDetailsAndConnections', async () => {
    await navigate('设备管理')
    const scopeResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/devices' && !new URL(response.url()).searchParams.get('siteId'))
    await page.getByLabel('授权局点').selectOption('')
    await scopeResponse
    await page.locator('[data-testid="device-inventory-table"] tbody tr').first().waitFor()
    await page.waitForTimeout(500)
    result.inventoryRows = await page.locator('[data-testid="device-inventory-table"] tbody tr').allTextContents()
    const devices = []
    result.deviceAttempts = devices
    for (const host of ['192.168.56.2', '192.168.56.5']) {
      const rows = page.locator('[data-testid="device-inventory-table"] tbody tr').filter({ hasText: host })
      await rows.first().waitFor()
      // Stable wireless inventory IDs distinguish ACs from older terminal-only
      // entries at the same simulator IP, whose credentials may be absent.
      const inventoryId = host.endsWith('.2') ? (process.env.R69_AC1_ID || 'r68-ac1') : (process.env.R69_AC2_ID || 'r68-ac2')
      const row = rows.filter({ hasText: inventoryId })
      if (await row.count() !== 1) throw new Error(`Expected exactly one inventory row for ${host}/${inventoryId}`)
      const detailResponse = page.waitForResponse(response => /^\/api\/system\/devices\/[^/]+$/.test(new URL(response.url()).pathname) && response.request().method() === 'GET')
      await row.getByRole('button', { name: '详情', exact: true }).click()
      const detail = await (await detailResponse).json()
      if (!detail.terminalProfileId || !detail.item) throw new Error(`No linked terminal profile for ${host}`)
      if (detail.item.id !== inventoryId) throw new Error(`Inventory detail identity mismatch for ${inventoryId}`)
      const dialog = page.getByRole('dialog', { name: '设备详情 / 审计' })
      await dialog.waitFor()
      const visibleDetail = await dialog.innerText()
      if (!visibleDetail.includes(host)) throw new Error(`Wrong detail context for ${host}`)
      await screenshots(host.endsWith('.2') ? 'device-ac1' : 'device-ac2')
      await dialog.getByRole('button', { name: 'Close this dialog' }).click()
      const connectionResponse = page.waitForResponse(response => new URL(response.url()).pathname.endsWith(`/profiles/${encodeURIComponent(detail.terminalProfileId)}/test-connection`) && response.request().method() === 'POST', { timeout: 240000 })
      await row.getByRole('button', { name: '测试连接', exact: true }).click()
      const connection = await (await connectionResponse).json()
      devices.push({ host, id: detail.item.id, profileId: detail.terminalProfileId, type: detail.item.deviceType, connection })
      if (host.endsWith('.2')) linkedDevice = { id: detail.item.id, profileId: detail.terminalProfileId }
    }
    await screenshots('devices-connected')
    if (devices.some(device => device.connection.status !== 'CONNECTED')) throw new Error(`Connection tests: ${devices.map(device => `${device.host} (${device.id}/${device.profileId}): ${device.connection.status}`).join('; ')}`)
    return { devices }
  })

  await step('wirelessCollectionAndZip', async () => {
    await navigate('无线业务')
    await page.locator('.wireless-tree__domain').first().waitFor()
    const domainInputs = page.locator('.wireless-task > div:nth-child(2) input[type="checkbox"]')
    const count = await domainInputs.count()
    if (count < 2) throw new Error(`Expected two visible domains, got ${count}`)
    for (let index = 0; index < count; index++) await domainInputs.nth(index).check()
    for (const input of await page.locator('.wireless-task input[type="checkbox"]').all()) {
      const value = await input.inputValue()
      if (value.startsWith('display ')) await input.setChecked((process.env.R69_COMMANDS ? process.env.R69_COMMANDS.split(',') : ['display version', 'display wlan ap all', 'display wlan ap all address']).includes(value))
    }
    const createResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/wireless/tasks' && response.request().method() === 'POST')
    await page.getByRole('button', { name: '开始只读采集', exact: true }).click()
    const created = await (await createResponse).json()
    activeTaskId = created.id
    if (!activeTaskId || created.acDeviceIds?.length !== 2) throw new Error('Task did not select exactly two ACs')
    await page.locator('.wireless-task-detail h3').waitFor()
    await screenshots('wireless-progress')
    await page.waitForFunction(() => /任务详情 · (SUCCEEDED|PARTIAL|FAILED|CANCELLED|TIMED_OUT)/.test(document.querySelector('.wireless-task-detail h3')?.textContent || ''), null, { timeout: 240000 })
    const visibleDetail = await page.locator('.wireless-task-detail').innerText()
    if (!visibleDetail.includes(activeTaskId)) throw new Error('Task detail changed unexpectedly')
    if (!visibleDetail.includes('任务详情 · SUCCEEDED')) throw new Error(`Wireless task did not succeed: ${visibleDetail.slice(0, 180)}`)
    await screenshots('wireless-complete')
    const downloadPromise = page.waitForEvent('download')
    await page.getByRole('button', { name: '下载 ZIP', exact: true }).click()
    const archive = path.join(output, 'ui-created-wireless-task.zip')
    await (await downloadPromise).saveAs(archive)
    const validation = 'import hashlib,json,sys,zipfile\nz=zipfile.ZipFile(sys.argv[1]); m=json.loads(z.read("manifest.json")); checks=[{"name":f["name"],"ok":len(z.read(f["name"]))==f["bytes"] and hashlib.sha256(z.read(f["name"])).hexdigest()==f["sha256"]} for f in m["files"]]; print(json.dumps({"names":z.namelist(),"manifest":m,"checks":checks,"observations":json.loads(z.read("ap-observations.json"))["items"]},ensure_ascii=True))'
    const zip = JSON.parse(execFileSync('python', ['-c', validation, archive], { encoding: 'utf8' }))
    if (!zip.checks.length || zip.checks.some(check => !check.ok)) throw new Error('ZIP byte/hash validation failed')
    return { id: activeTaskId, domainIds: created.domainIds, acDeviceIds: created.acDeviceIds, commands: created.commands, visibleDetail, zip }
  })

  await step('apObservationsAndHistory', async () => {
    // Re-enter to load the newly published collection, preserving real UI refresh behavior.
    await navigate('运行总览')
    await navigate('无线业务')
    await page.locator('.wireless-tree__member').first().waitFor()
    const sources = []
    const members = page.locator('.wireless-tree__member')
    const count = await members.count()
    for (let index = 0; index < count; index++) {
      const member = members.nth(index)
      const label = await member.innerText()
      await member.click()
      await page.locator('.wireless-table tbody tr').first().waitFor()
      await page.locator('.wireless-table tbody tr').first().click()
      await page.locator('.wireless-source-history').first().waitFor()
      const expectedObservation = result.checks.wirelessCollectionAndZip?.zip?.observations?.find(observation => label.includes(observation.sourceAcDeviceId))
      if (expectedObservation) await page.waitForFunction(ref => document.querySelector('.wireless-observation-list')?.textContent?.includes(ref), expectedObservation.rawManifestRef)
      const observationText = await page.locator('.wireless-observation-list').innerText()
      const runtimeText = await page.locator('.wireless-runtime').innerText()
      if (!observationText.includes('raw=') || !observationText.includes('parser=')) throw new Error('Observation lacks raw/parser provenance')
      if (!runtimeText.includes('AP 最近有效状态') || !runtimeText.includes('最近有效状态')) throw new Error('History not loaded')
      const collection = page.locator('.wireless-collection-row').first()
      if (await collection.count()) {
        await collection.click()
        await page.locator('.wireless-collection-item').first().waitFor()
        if (expectedObservation) await page.waitForFunction(ref => document.querySelector('.wireless-collection-detail')?.textContent?.includes(ref), expectedObservation.rawManifestRef)
      }
      sources.push({ label, apRows: await page.locator('.wireless-table tbody tr').count(), expectedRawManifestRef: expectedObservation?.rawManifestRef, observationText, runtimeText, collectionText: await page.locator('.wireless-collection').innerText() })
      await screenshots(`wireless-ac${index + 1}-history`)
    }
    if (count !== 2) throw new Error(`Expected two AC source views, got ${count}`)
    return { sources }
  })

  await step('configBackupsAndDiff', async () => {
    if (!linkedDevice) throw new Error('No verified AC profile from device-detail step')
    await navigate('设备管理')
    await page.getByLabel('授权局点').selectOption('')
    const row = page.locator('[data-testid="device-inventory-table"] tbody tr').filter({ hasText: linkedDevice.id }).first()
    await row.getByRole('button', { name: '详情', exact: true }).click()
    await page.getByRole('dialog', { name: '设备详情 / 审计' }).getByRole('button', { name: '查看配置备份', exact: true }).click()
    await page.getByRole('heading', { name: '配置备份', exact: true }).waitFor()
    const targetCard = page.locator('section.card').filter({ has: page.getByRole('heading', { name: '当前设备', exact: true }) })
    await targetCard.locator('label.check-row input[type="checkbox"]').first().waitFor()
    await page.getByLabel('配置类别', { exact: true }).selectOption('RUNNING')
    const selectedProfiles = targetCard.locator('label.check-row input[type="checkbox"]')
    if (await selectedProfiles.count() !== 1 || await selectedProfiles.first().inputValue() !== linkedDevice.profileId) throw new Error(`Config backup context differs from verified device ${linkedDevice.id}/${linkedDevice.profileId}: ${await selectedProfiles.evaluateAll(elements => elements.map(element => element.value))}`)
    await selectedProfiles.first().check()
    const batches = []
    for (let round = 0; round < 2; round++) {
      const createResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/config-backups' && response.request().method() === 'POST')
      await page.getByRole('button', { name: '开始配置备份', exact: true }).click()
      const batch = await (await createResponse).json()
      batches.push({ id: batch.id, profileId: linkedDevice.profileId, kind: batch.configurationKind })
      await page.getByRole('button', { name: '开始配置备份', exact: true }).waitFor({ state: 'visible' })
      await page.waitForFunction(() => [...document.querySelectorAll('.toolbar .muted')].some(element => /批次 \d+\/\d+ · (SUCCEEDED|FAILED|PARTIAL)/.test(element.textContent || '')), null, { timeout: 240000 })
      if (!await page.locator('.toolbar .muted').filter({ hasText: /批次 1\/1 · SUCCEEDED/ }).count()) throw new Error(`Config backup ${batch.id} did not succeed`)
      await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent?.trim() === '开始配置备份' && !button.disabled && !button.classList.contains('is-loading')))
    }
    const history = page.locator('table[aria-label="配置版本历史"] tbody tr')
    if (await history.count() < 2) throw new Error('Two backup snapshots were not visible')
    const historyText = await history.allTextContents()
    await history.first().getByRole('button', { name: '查看', exact: true }).click()
    await page.locator('[aria-label="配置内容"] pre').waitFor()
    const content = await page.locator('[aria-label="配置内容"] pre').innerText()
    const contentEvidence = { bytes: Buffer.byteLength(content), hasRedactionMarker: /REDACTED|redacted|\*{3,}|脱敏/.test(content), hasH3cConfig: /sysname|version|interface/.test(content) }
    if (!contentEvidence.hasH3cConfig) throw new Error('Backup did not contain recognizable configuration')
    if (!contentEvidence.hasRedactionMarker) throw new Error('Expected sensitive configuration fields to be redacted')
    await page.getByRole('button', { name: '比较', exact: true }).click()
    await page.locator('.diff-grid').waitFor()
    const diffText = await page.locator('.diff-grid').innerText()
    await screenshots('config-backup-diff')
    return { deviceId: linkedDevice.id, profileId: linkedDevice.profileId, batches, historyText, contentEvidence, diffText, note: 'Two read-only captures; no device configuration was changed to manufacture a diff.' }
  })

  await step('simulatorCompleteConfiguration', async () => {
    if (!linkedDevice) {
      await navigate('设备管理')
      const inventoryResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/devices' && !new URL(response.url()).searchParams.get('siteId'))
      await page.getByLabel('授权局点').selectOption('')
      await inventoryResponse
      const inventoryId = process.env.R69_AC1_ID || 'r68-ac1'
      const row = page.locator('[data-testid="device-inventory-table"] tbody tr').filter({ hasText: inventoryId })
      const detailResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/system/devices/${inventoryId}` && response.request().method() === 'GET')
      await row.getByRole('button', { name: '详情', exact: true }).click()
      const detail = await (await detailResponse).json()
      if (detail.item?.id !== inventoryId || detail.item?.managementIp !== '192.168.56.2' || !detail.terminalProfileId) throw new Error('Complete configuration context is not the authorized AC1')
      linkedDevice = { id: inventoryId, profileId: detail.terminalProfileId }
      await page.getByRole('dialog', { name: '设备详情 / 审计' }).getByRole('button', { name: '查看配置备份', exact: true }).click()
    }
    const panel = page.locator('section[aria-label="模拟器完整配置"]')
    await panel.waitFor()
    const previousResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/simulator-config/backups' && response.request().method() === 'GET')
    await panel.getByRole('button', { name: '刷新完整备份', exact: true }).click()
    const previous = await (await previousResponse).json()
    const previousVersion = Math.max(0, ...previous.map(item => item.version || 1))
    await page.waitForFunction(() => !document.querySelector('[aria-label="完整配置基线"]')?.disabled)
    const captureResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/system/simulator-config/backups/${linkedDevice.profileId}` && response.request().method() === 'POST', { timeout: 240000 })
    await panel.getByRole('button', { name: '采集完整配置', exact: true }).click()
    const capture = await captureResponse
    const backup = await capture.json()
    if (!capture.ok() || backup.profileId !== linkedDevice.profileId || backup.deviceId !== linkedDevice.id || backup.integrity !== 'COMPLETE_HASH_VERIFIED' || backup.state !== 'SUCCEEDED' || !backup.restorable || !backup.rawByteCount || !backup.rawSha256) throw new Error(`Complete backup did not return verified AC1 evidence: ${capture.status()}/${backup.integrity}/${backup.restorable}`)
    if (backup.version !== previousVersion + 1) throw new Error(`Complete configuration version did not increment: ${previousVersion} -> ${backup.version}`)
    const baseline = panel.getByLabel('完整配置基线')
    await baseline.locator(`option[value="${backup.id}"]`).waitFor({ state: 'attached' })
    await page.waitForFunction(() => !document.querySelector('[aria-label="完整配置基线"]')?.disabled)
    await baseline.selectOption('')
    if (await panel.getByRole('button', { name: '查看脱敏完整配置', exact: true }).isEnabled()) throw new Error('Complete view must require a selected baseline')
    await baseline.selectOption(backup.id)
    const viewResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/system/simulator-config/backups/${backup.id}/view` && response.request().method() === 'GET')
    await panel.getByRole('button', { name: '查看脱敏完整配置', exact: true }).click()
    const view = await viewResponse
    if (!view.ok()) throw new Error(`Complete backup view failed: ${view.status()}`)
    await panel.locator('pre').waitFor()
    const content = await panel.locator('pre').innerText()
    const contentEvidence = { bytes: Buffer.byteLength(content), hasRedactionMarker: /REDACTED|redacted|\*{3,}|脱敏/.test(content), hasH3cConfig: /sysname|version|interface/.test(content) }
    if (!contentEvidence.hasH3cConfig || !contentEvidence.hasRedactionMarker) throw new Error('Complete configuration view lacks H3C content/redaction evidence')
    if (await panel.getByRole('button', { name: '执行接口描述变更', exact: true }).isEnabled()) throw new Error('Configuration writer enabled without explicit inputs/confirmation')
    if (await panel.getByRole('checkbox', { name: '确认在当前模拟器修改指定接口描述' }).isChecked()) throw new Error('Configuration confirmation was unexpectedly selected')
    await screenshots('complete-config-baseline-view')
    return { backup, previousVersion, selectedBaseline: await baseline.inputValue(), contentEvidence, writeButtonDisabled: true, note: 'Captured configuration using read-only device commands; baseline selected and redacted view opened. No description write/restore/rollback action was invoked.' }
  })

  await step('taskHistoryFilters', async () => {
    await navigate('无线业务')
    const listResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/wireless/tasks' && response.request().method() === 'GET')
    await page.locator('.wireless-task-history .card__header').getByRole('button', { name: '刷新', exact: true }).click()
    const tasks = await (await listResponse).json()
    const scoped = tasks.find(task => task.siteIds?.length && task.acDeviceIds?.length)
    if (!scoped) throw new Error('Live task list has no frozen Site/AC metadata to verify')
    const site = scoped.siteIds[0], ac = scoped.acDeviceIds[0], state = scoped.state
    const filters = page.locator('.wireless-history-filters')
    await filters.getByRole('button', { name: '清除筛选', exact: true }).click()
    await page.getByLabel('任务历史 Site', { exact: true }).selectOption(site)
    await page.getByLabel('任务历史 AC', { exact: true }).selectOption(ac)
    await page.getByLabel('任务历史状态', { exact: true }).selectOption(state)
    const expected = tasks.filter(task => task.siteIds?.includes(site) && task.acDeviceIds?.includes(ac) && task.state === state).map(task => task.id)
    const rows = page.locator('.wireless-task-row')
    const actual = await rows.evaluateAll(elements => elements.map(element => element.getAttribute('data-task-id')))
    if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error('Site/AC/state history filter does not match real task metadata')
    await screenshots('task-history-filters')
    const absentState = ['QUEUED', 'RUNNING', 'CANCELLED', 'TIMED_OUT', 'INTERRUPTED', 'FAILED', 'PARTIAL', 'SUCCEEDED'].find(candidate => !tasks.some(task => task.siteIds?.includes(site) && task.acDeviceIds?.includes(ac) && task.state === candidate))
    if (absentState) {
      await page.getByLabel('任务历史状态', { exact: true }).selectOption(absentState)
      await page.getByText('没有符合筛选条件的任务', { exact: true }).waitFor()
      if (await rows.count()) throw new Error('Expected empty filtered task history')
    }
    await filters.getByRole('button', { name: '清除筛选', exact: true }).click()
    if (await rows.count() !== tasks.length) throw new Error('Clear filters did not restore loaded history')
    return { site, ac, state, matchedTaskIds: actual, loadedCount: tasks.length, absentState, clearRestoredCount: await rows.count() }
  })
} catch (error) { result.error = String(error) }
finally {
  await page.unrouteAll({ behavior: 'wait' })
  await Promise.allSettled([...responseReads])
  result.finishedAt = new Date().toISOString()
  result.overall = !result.error && Object.values(result.checks).every(check => check.status === 'PASS') && !result.apiFailures.length && !result.pageErrors.length && !result.consoleErrors.length ? 'PASS_WITH_LIMITATIONS' : 'PARTIAL'
  save()
  await browser.close()
}
console.log(JSON.stringify({ overall: result.overall, checks: Object.fromEntries(Object.entries(result.checks).map(([key, value]) => [key, { status: value.status, error: value.error }])), apiFailures: result.apiFailures, pageErrors: result.pageErrors, consoleErrors: result.consoleErrors, output }, null, 2))
if (result.overall === 'PARTIAL') process.exitCode = 1
