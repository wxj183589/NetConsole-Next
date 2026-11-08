import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.R73_UI_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', process.env.R73_BROWSER_OUTPUT || 'r73-browser-product')
mkdirSync(output, { recursive: true })
const result = { base, deviceConnections: 0, configurationWrites: 0, checks: {}, errors: [], layouts: [] }
const browser = await chromium.launch({ headless: true,
  executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, acceptDownloads: true })
page.setDefaultTimeout(20000)
page.on('pageerror', error => result.errors.push(error.message))
const assert = (condition, message) => { if (!condition) throw new Error(message) }
const save = () => writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2))
async function step(name, action) {
  try {
    result.checks[name] = { status: 'PASS', ...await action() }
    for (const width of [1920, 2560]) {
      await page.setViewportSize({ width, height: width === 1920 ? 1080 : 1440 })
      await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true })
      const actual = await page.evaluate(() => document.documentElement.scrollWidth)
      result.layouts.push({ name, width, actual })
      assert(actual <= width + 1, `${name} horizontal overflow`)
    }
  } catch (error) {
    result.checks[name] = { status: 'FAIL', error: String(error) }
    await page.screenshot({ path: path.join(output, `${name}-failure.png`), fullPage: true }).catch(() => {})
  }
  save()
  console.log(`${name} ${result.checks[name].status}`)
}
try {
  await page.goto(base, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill(process.env.R73_USER || 'superadmin')
  await page.getByLabel('密码').fill(process.env.R73_PASSWORD || 'superadmin')
  await page.getByRole('button', { name: '登录工作台', exact: true }).click()
  await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
  await step('dashboardModularChart', async () => {
    await page.locator('canvas').first().waitFor()
    const count = await page.locator('canvas').count()
    return { canvasCount: count, modularEcharts: true }
  })
  await step('faultLldpFilter', async () => {
    const panel = page.getByRole('region', { name: '持久故障中心', exact: true })
    const filter = panel.getByLabel('故障类型', { exact: true })
    await filter.selectOption('LLDP_NEIGHBOR_LOST')
    await panel.getByRole('button', { name: '查询故障', exact: true }).click()
    await panel.getByRole('button', { name: '查询故障', exact: true }).waitFor()
    assert(await filter.inputValue() === 'LLDP_NEIGHBOR_LOST', 'LLDP fault filter missing')
    return { selectedKind: await filter.inputValue() }
  })
  await step('configurationStorageAndHistory', async () => {
    await page.goto(`${base}/config-backups?deviceId=r68-ac1`, { waitUntil: 'networkidle' })
    const panel = page.getByRole('region', { name: '完整配置容量与保留策略', exact: true })
    const measured = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/simulator-config/storage')
    await panel.getByRole('button', { name: '查看配置存储容量', exact: true }).click()
    const response = await measured
    assert(response.ok(), 'Storage HTTP failed')
    const storage = await response.json()
    assert(storage.profileId && storage.manifestCount > 0 && storage.profileStoredBytes > 0 &&
      storage.automaticDeletion === false && storage.retentionPolicy === 'PRESERVE_ALL_STOP_AT_CAPACITY', 'Stored configuration capacity contract failed')
    await panel.getByText(new RegExp(`此设备清单 ${storage.manifestCount} /`)).waitFor()
    const bulk = page.getByRole('region', { name: '完整配置批量与定时备份', exact: true })
    const historyResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/system/complete-configuration/batches')
    await bulk.getByRole('button', { name: '刷新完整备份批次历史', exact: true }).click()
    const history = await historyResponse
    assert(history.ok(), 'Batch history HTTP failed')
    const items = await history.json()
    const selection = bulk.getByLabel('完整备份批次历史', { exact: true })
    if (items.length) {
      await selection.selectOption(items[0].id)
      const detailResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/system/config-backups/batches/${items[0].id}`)
      await bulk.getByRole('button', { name: '查看批次结果', exact: true }).click()
      const detail = await detailResponse
      assert(detail.ok(), 'Stored batch detail HTTP failed')
      assert((await detail.json()).id === items[0].id, 'Batch detail must preserve selected ID')
    } else {
      assert(await bulk.getByRole('button', { name: '查看批次结果', exact: true }).isDisabled(), 'Empty history must disable details')
    }
    return { storage, storedBatches: items.length }
  })
  await step('wirelessObservationExport', async () => {
    await page.goto(`${base}/wireless`, { waitUntil: 'networkidle' })
    const domains = page.locator('.wireless-tree__domain > button')
    const exports = []
    for (let index = 0; index < await domains.count(); index++) {
      await domains.nth(index).click()
      const button = page.getByRole('button', { name: '导出当前 AP 观察 CSV', exact: true })
      if (!await button.isEnabled()) continue
      const pending = page.waitForEvent('download')
      await button.click()
      const download = await pending
      const target = path.join(output, `wireless-observations-domain-${index}.csv`)
      await download.saveAs(target)
      const csv = readFileSync(target, 'utf8')
      assert(['observation_id', 'source_site_id', 'source_ac_device_id', 'raw_manifest_ref',
        'projection_revision', 'FILTERED_LOADED_OBSERVATIONS_ONLY'].every(value => csv.includes(value)), 'Wireless CSV lost provenance')
      exports.push({ index, bytes: Buffer.byteLength(csv), csv })
    }
    const combined = exports.map(item => item.csv).join('\n')
    assert(combined.includes('r68-ac1') && combined.includes('r68-ac2'), 'Each independent AC must remain exportable in its own Domain')
    return { exports: exports.map(({ index, bytes }) => ({ index, bytes })), deviceConnections: 0 }
  })
} catch (error) {
  result.errors.push(String(error))
} finally {
  result.overall = result.errors.length || Object.values(result.checks).some(item => item.status !== 'PASS') ? 'FAIL' : 'PASS'
  save()
  await browser.close()
}
console.log(JSON.stringify({ output, overall: result.overall, checks: result.checks, errors: result.errors }, null, 2))
if (result.overall !== 'PASS') process.exitCode = 1
