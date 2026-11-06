// Owned source Vite + Chrome and synthetic API fixture; no service, device, or business-data access.
import { chromium, expect } from '../web/node_modules/@playwright/test/index.mjs'
import { createServer } from '../web/node_modules/vite/dist/node/index.js'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

const repo = path.resolve(import.meta.dirname, '..')
const output = workspacePath('build', 'task-diagnostics-browser')
fs.mkdirSync(output, { recursive: true })
const scenarios = []; const screenshots = []; const requests = []; const errors = []; const unexpected = []; const downloads = []
const viewports = [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]
const result = { status: 'RUNNING', evidenceType: 'CHROME_SOURCE_UI_MOCKED_API_FIXTURE', realDevice: 'REAL_DEVICE_PENDING',
  backendExercised: false, deviceConnections: 0, databaseWrites: 0, startedAt: new Date().toISOString(),
  viewports, deviceScaleFactor: 1, screenshotMode: 'VIEWPORT', scenarios, screenshots, requests, errors, unexpected, downloads,
  resources: { vitePort: null, ownedViteClosed: false, ownedChromeClosed: false } }
const runId = 'mock-diagnostic-run'; const otherId = 'mock-other-run'
const commands = ['display version', 'display vlan all', 'display current-configuration', 'display wlan mesh-profile', 'display device']
const raw = ['<mock>display version\nH3C Comware Software (synthetic fixture)\n<mock>\n', '<mock>display vlan all\nSynthetic partial VLAN output\n']
const details = commands.map((command, index) => ({ index, command, state: index === 4 ? 'FAILED' : 'SUCCEEDED',
  captureStatus: index === 4 ? null : 'COMPLETE', parseStatus: index === 0 ? 'UNRECOGNIZED' : index === 1 ? 'PARTIAL' : 'PARSED' }))
function manifest(id = runId) {
  return { version: 1, runId: id, taskState: 'PARTIAL', status: id === runId ? 'PARTIAL' : 'UNAVAILABLE', canDownload: id === runId,
    commands: id !== runId ? [] : details.map(item => ({ index: item.index, command: item.command,
      availability: ['READY', 'READY', 'CONFIG_EXCLUDED', 'UNSUPPORTED_COMMAND', 'NO_RAW'][item.index], canDownload: item.index < 2,
      captureStatus: item.captureStatus, parseStatus: item.parseStatus, capturedMs: 1_800_000_000_000,
      byteCount: item.index < 2 ? Buffer.byteLength(raw[item.index]) : null, outputSha256: null, segmentSha256: null,
      downloadByteCount: item.index < 2 ? Buffer.byteLength(raw[item.index]) : null, downloadSha256: null, redacted: false })) }
}
const zipBytes = execFileSync('python', ['-c', 'import io,json,sys,zipfile\np=json.load(sys.stdin)\nb=io.BytesIO()\nwith zipfile.ZipFile(b,"w",zipfile.ZIP_STORED) as z:\n z.writestr("manifest.json",json.dumps(p["manifest"],ensure_ascii=False))\n for i,text in enumerate(p["raw"]): z.writestr(f"command-{i+1:03}.txt",text)\nsys.stdout.buffer.write(b.getvalue())'],
  { input: JSON.stringify({ manifest: manifest(), raw }) })
let mode = 'normal'; let denyManifest = false; let heldDownload; let heldManifest
let server; let browser; let page; let failure
try {
  server = await createServer({ configFile: path.join(repo, 'web/vite.config.ts'), root: path.join(repo, 'web'), server: { host: '127.0.0.1', port: 0 } })
  await server.listen(); result.resources.vitePort = server.httpServer.address().port
  const base = `http://127.0.0.1:${result.resources.vitePort}`
  browser = await chromium.launch({ headless: true, executablePath: process.env.NETCONSOLE_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
  page = await browser.newPage({ viewport: viewports[0], deviceScaleFactor: 1, acceptDownloads: true })
  page.on('pageerror', error => errors.push(error.message))
  page.on('download', download => downloads.push(download.suggestedFilename()))
  await page.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url()); const endpoint = url.pathname
    if (!endpoint.startsWith('/api/')) return route.continue()
    requests.push({ method: request.method(), endpoint, query: url.search })
    let status = 200; let body
    if (endpoint === '/api/auth/me') body = { username: 'fixture-reader', role: 'SUPERADMIN' }
    else if (endpoint === '/api/auth/csrf') body = { csrfToken: 'synthetic-diagnostic-csrf' }
    else if (endpoint === '/api/system/health') body = { status: 'running', healthState: 'HEALTHY', collectionState: 'running', instanceId: 'synthetic-diagnostics-ui' }
    else if (endpoint === '/api/system/modules') body = ['tasks', 'devices'].map(key => ({ key, enabled: true }))
    else if (endpoint === '/api/system/devices') body = { items: [], total: 0, page: 1, pageSize: 1, hasMore: false, summary: { totalRecords: 0 } }
    else if (endpoint === '/api/system/h3c/profiles') body = []
    else if (endpoint === '/api/system/tasks') body = [runId, otherId].map(id => ({ id, taskType: 'H3C_SSH_READONLY', state: 'PARTIAL', captureStatus: 'PARTIAL', parseStatus: 'PARTIAL', queuedMs: 1_800_000_000_000 }))
    else if (/\/commands$/.test(endpoint)) body = endpoint.includes(runId) ? details : []
    else if (/\/diagnostics$/.test(endpoint)) {
      const id = endpoint.split('/')[4]
      if (denyManifest) { status = 403; body = { error: 'FORBIDDEN', detail: 'Synthetic scope revocation' } }
      else if (mode === 'hold-manifest' && id === runId) { heldManifest = route; return }
      else body = manifest(id)
    } else if (/\/diagnostics\/download$/.test(endpoint)) {
      if (mode === 'corrupt') { status = 409; body = { error: 'DIAGNOSTIC_RAW_INVALID', detail: 'Synthetic finalized segment mismatch' } }
      else if (mode === 'hold-download') { heldDownload = route; return }
      else {
        if (mode === 'revoke-after-body') denyManifest = true
        const index = url.searchParams.get('commandIndex'); const text = index !== null
        return route.fulfill({ status: 200, contentType: text ? 'text/plain; charset=utf-8' : 'application/zip',
          headers: { 'cache-control': 'no-store', 'content-disposition': `attachment; filename="diagnostics-${runId}${text ? '-command-001.txt' : '.zip'}"` },
          body: text ? raw[Number(index)] : zipBytes })
      }
    } else { status = 404; body = {}; unexpected.push(`${request.method()} ${endpoint}`) }
    await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) })
  })
  const dialog = page.getByRole('dialog')
  async function open(index = 0) {
    await page.getByRole('button', { name: '命令明细', exact: true }).nth(index).click()
    await expect(dialog).toBeVisible()
    if (mode !== 'hold-manifest' || index !== 0) {
      const bundle = dialog.getByRole('button', { name: '下载诊断 ZIP', exact: true })
      if (index === 0) await expect(bundle).toBeEnabled()
      else { await expect(dialog).toContainText('该任务暂无命令级记录。'); await expect(bundle).toBeDisabled() }
    }
  }
  async function close() { await dialog.locator('.el-dialog__headerbtn').click(); await expect(dialog).not.toBeVisible() }
  async function screenshot(name) { await page.screenshot({ path: path.join(output, name), fullPage: false, animations: 'disabled' }); screenshots.push({ file: name, viewport: page.viewportSize() }) }
  await page.goto(`${base}/tasks`)
  await expect(page.getByRole('heading', { name: '任务中心', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '命令明细', exact: true })).toHaveCount(2)
  await open()
  await expect(dialog.getByRole('button', { name: '下载 TXT', exact: true })).toHaveCount(2)
  await expect(dialog).toContainText('配置内容已排除'); await expect(dialog).toContainText('暂不支持下载'); await expect(dialog).toContainText('暂无输出')
  await expect(dialog).toContainText('格式未识别'); await expect(dialog).toContainText('识别不完整')
  expect(requests.filter(item => item.method !== 'GET')).toEqual([])
  expect(requests.filter(item => item.endpoint.endsWith('/download'))).toEqual([])
  scenarios.push('manifest-authorizes-only-ready-readonly-commands-and-excludes-configuration')
  for (const viewport of viewports) {
    await page.setViewportSize(viewport)
    const box = await dialog.boundingBox()
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(viewport.width)
    expect(await dialog.locator('.table-wrap').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true)
    await screenshot(`diagnostics-dialog-${viewport.width}.png`)
  }
  scenarios.push('command-dialog-fits-1920x1080-and-2560x1440')
  const txtEvent = page.waitForEvent('download')
  await dialog.getByRole('button', { name: '下载 TXT', exact: true }).first().click()
  const txt = await txtEvent; expect(txt.suggestedFilename()).toBe(`diagnostics-${runId}-command-001.txt`)
  await txt.saveAs(path.join(output, txt.suggestedFilename()))
  expect(fs.readFileSync(path.join(output, txt.suggestedFilename()), 'utf8')).toBe(raw[0])
  scenarios.push('actual-chrome-txt-download-preserves-synthetic-readonly-output')
  const zipEvent = page.waitForEvent('download')
  await dialog.getByRole('button', { name: '下载诊断 ZIP', exact: true }).click()
  const zip = await zipEvent; expect(zip.suggestedFilename()).toBe(`diagnostics-${runId}.zip`)
  await zip.saveAs(path.join(output, zip.suggestedFilename()))
  const members = JSON.parse(execFileSync('python', ['-c', 'import json,sys,zipfile\nwith zipfile.ZipFile(sys.argv[1]) as z:\n print(json.dumps({"names":z.namelist(),"text":z.read("command-001.txt").decode(),"manifest":json.loads(z.read("manifest.json"))}))', path.join(output, zip.suggestedFilename())], { encoding: 'utf8' }))
  expect(members.names).toEqual(['manifest.json', 'command-001.txt', 'command-002.txt']); expect(members.text).toBe(raw[0]); expect(members.manifest).toEqual(manifest())
  scenarios.push('actual-chrome-zip-download-contains-only-two-eligible-commands-and-manifest')
  await close(); mode = 'corrupt'; await open()
  await dialog.getByRole('button', { name: '下载 TXT', exact: true }).first().click()
  await expect(dialog.getByRole('alert')).toHaveText('诊断输出校验失败，暂不可下载。')
  await expect(dialog.getByRole('button', { name: '下载 TXT', exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: '下载诊断 ZIP', exact: true })).toBeDisabled()
  await screenshot('diagnostics-integrity-failed.png'); expect(downloads).toHaveLength(2)
  scenarios.push('integrity-rejection-clears-eligibility-and-shows-chinese-error')
  await close(); mode = 'revoke-after-body'; await open()
  await dialog.getByRole('button', { name: '下载 TXT', exact: true }).first().click()
  await expect(dialog.getByRole('alert')).toHaveText('任务不存在或访问权限已变化，请刷新任务列表。')
  await expect(dialog).not.toContainText('display version'); expect(downloads).toHaveLength(2)
  await screenshot('diagnostics-scope-revoked.png')
  scenarios.push('received-blob-is-discarded-when-final-manifest-read-revokes-scope')
  await close(); denyManifest = false; mode = 'hold-download'; await open()
  await dialog.getByRole('button', { name: '下载 TXT', exact: true }).first().click()
  await expect.poll(() => Boolean(heldDownload)).toBe(true)
  await close()
  await heldDownload.fulfill({ status: 200, contentType: 'text/plain', body: raw[0] }).catch(() => {})
  mode = 'normal'; await open(1)
  await expect(dialog).toContainText('该任务暂无命令级记录。')
  await expect(dialog.getByRole('button', { name: '下载诊断 ZIP', exact: true })).toBeDisabled(); expect(downloads).toHaveLength(2)
  scenarios.push('closing-dialog-aborts-late-body-and-next-task-has-no-old-downloads')
  await close(); mode = 'hold-manifest'; await open()
  await expect.poll(() => Boolean(heldManifest)).toBe(true)
  await close(); mode = 'normal'; await open(1)
  await heldManifest.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(manifest()) }).catch(() => {})
  await expect(dialog).not.toContainText('display version')
  await expect(dialog.getByRole('button', { name: '下载诊断 ZIP', exact: true })).toBeDisabled(); expect(downloads).toHaveLength(2)
  scenarios.push('late-first-task-manifest-cannot-enable-downloads-in-second-task')
  expect(requests.filter(item => item.method !== 'GET')).toEqual([])
  expect(errors).toEqual([]); expect(unexpected).toEqual([])
  expect(requests.filter(item => item.endpoint.endsWith('/download')).every(item => !item.query || item.query === '?commandIndex=0')).toBe(true)
  result.status = 'PASS'; fs.rmSync(path.join(output, 'failure.png'), { force: true })
} catch (error) {
  failure = error; result.status = 'FAIL'; result.failure = error instanceof Error ? error.message : String(error)
  if (page && !page.isClosed()) {
    await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: false }).catch(() => {})
    console.error(JSON.stringify({ errors, unexpected, url: page.url(), body: (await page.locator('body').innerText()).slice(0, 12000) }))
  }
} finally {
  try { await browser?.close(); result.resources.ownedChromeClosed = Boolean(browser) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.chromeCleanupError = String(error) }
  try { await server?.close(); result.resources.ownedViteClosed = Boolean(server) }
  catch (error) { failure ||= error; result.status = 'FAIL'; result.viteCleanupError = String(error) }
  result.completedAt = new Date().toISOString()
  fs.writeFileSync(path.join(output, 'acceptance.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result))
}
if (failure) throw failure
