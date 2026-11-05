import { chromium } from '../web/node_modules/playwright/index.mjs'
import { readFile } from 'node:fs/promises'
import { join } from 'node:path'

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL
const sshPort = process.env.NETCONSOLE_E2E_SSH_PORT
const telnetPort = process.env.NETCONSOLE_E2E_TELNET_PORT
if (!baseUrl || !sshPort || !telnetPort) throw new Error('E2E base URL and simulator ports are required')

const executablePath = process.env.NETCONSOLE_E2E_BROWSER
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ['--no-sandbox', ...(process.env.NETCONSOLE_E2E_WEBROOT ? ['--disable-features=LocalNetworkAccessChecks'] : [])] })
const context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] })
if (process.env.NETCONSOLE_E2E_WEBROOT) {
  const webroot = process.env.NETCONSOLE_E2E_WEBROOT
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url())
    if (url.origin !== baseUrl || route.request().method() !== 'GET') return route.continue()
    const path = url.pathname.startsWith('/app/assets/') ? url.pathname.slice(1) :
      ['/', '/login', '/terminal'].includes(url.pathname) ? 'index.html' : null
    if (!path || path.includes('..')) return route.continue()
    try {
      const body = await readFile(join(webroot, path))
      const contentType = path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html'
      await route.fulfill({ status: 200, contentType, body })
    } catch { await route.continue() }
  })
}
const page = await context.newPage()
const consoleErrors = []
const failedRequests = []
const badResponses = []
const apiErrors = []
const sessionIds = []
const sshResizeFrames = []
let terminalSockets = 0
page.on('websocket', (socket) => {
  if (!socket.url().includes('/api/system/terminal/sessions/')) return
  terminalSockets += 1
  if (terminalSockets !== 1) return
  socket.on('framesent', (event) => {
    try {
      const message = JSON.parse(event.payload)
      if (message.type === 'resize') sshResizeFrames.push([message.columns, message.rows])
    } catch { /* binary and non-JSON frames are irrelevant */ }
  })
})
let invalidTicketProbe = false
page.on('console', (message) => {
  const text = message.text()
  const expectedTicketRejection = text.includes('/sessions/missing/stream?ticket=invalid')
  const expectedTicketAssetError = invalidTicketProbe && text.includes('Failed to load resource')
  const expectedStaticAssetMiss = text === 'Failed to load resource: the server responded with a status of 404 (Not Found)'
  if (message.type() === 'error' && !expectedTicketRejection && !expectedTicketAssetError && !expectedStaticAssetMiss && !text.includes('401')) consoleErrors.push(text)
})
page.on('requestfailed', (request) => {
  failedRequests.push(`${request.method()} ${request.url()} :: ${request.failure()?.errorText || 'failed'}`)
})
page.on('response', (response) => {
  const status = response.status()
  const url = response.url()
  if (status >= 500) badResponses.push(`${status} ${url}`)
  const expectedAuthProbe = url.endsWith('/api/auth/me') && status === 401
  if (url.includes('/api/') && status >= 400 && !expectedAuthProbe && !url.includes('/sessions/missing/stream?ticket=invalid')) {
    apiErrors.push(`${status} ${url}`)
  }
})
page.on('request', (request) => {
  if (request.url().includes('cdn.jsdelivr.net')) throw new Error(`CDN request: ${request.url()}`)
})
const waitForTerminal = (value) => page.waitForFunction((text) => {
  const compact = (input) => input.replace(/\s+/g, '')
  return [...document.querySelectorAll('.xterm-rows')].some((rows) => compact(rows.innerText).includes(compact(text)))
}, value, { timeout: 15000 })
const waitForVisibleTerminal = (value) => page.locator('.terminal-pane:visible .xterm-rows').filter({ hasText: value }).last().waitFor({ timeout: 15000 })

async function selectDevice(id, protocol) {
  await page.locator(`[data-testid="terminal-device-row"][data-device-id="${id}"]`).click()
  await page.getByTestId('protocol-switch').getByRole('button', { name: protocol, exact: true }).click()
  const created = page.waitForResponse((response) => response.url().endsWith('/api/system/terminal/sessions') && response.request().method() === 'POST')
  await page.getByTestId('terminal-open-button').click()
  await page.locator('.terminal-tab').filter({ hasText: protocol }).last().waitFor()
  const response = await created
  if (response.status() !== 201) throw new Error(`terminal create returned ${response.status()}`)
  const { sessionId } = await response.json()
  sessionIds.push(sessionId)
  const deadline = Date.now() + 45000
  while (Date.now() < deadline) {
    const status = await context.request.get(`${baseUrl}/api/system/terminal/sessions/${sessionId}`)
    if (status.ok()) {
      const session = await status.json()
      if (session.state === 'FAILED') throw new Error(`terminal connection failed: ${session.connectionResult}`)
      if (session.state === 'CONNECTED') return sessionId
    }
    await new Promise((resolve) => setTimeout(resolve, 250))
  }
  throw new Error(`terminal connection timed out: ${sessionId}`)
}

async function typeCommand(command) {
  const input = page.locator('.terminal-pane:visible').last().locator('.xterm-helper-textarea')
  await input.focus()
  await page.keyboard.insertText(command)
  await page.keyboard.press('Enter')
}

try {
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名').fill('admin')
  await page.getByLabel('密码').fill('admin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()

  const unauthenticated = await browser.newContext().then(async (unauthContext) => {
    const response = await unauthContext.request.get(`${baseUrl}/api/system/terminal/devices`)
    await unauthContext.close()
    return response.status()
  })
  if (unauthenticated !== 401) throw new Error(`unauthenticated API status was ${unauthenticated}`)

  await page.locator('.app-sidebar').getByRole('button', { name: '终端控制台' }).click()
  await page.locator('.terminal-workbench').waitFor()
  const sshId = '00000000000000000000000000000001'
  const telnetId = '00000000000000000000000000000002'
  const autoId = '00000000000000000000000000000003'
  await page.getByTestId('terminal-device-row').nth(2).waitFor()
  if (await page.getByTestId('terminal-device-row').count() !== 3) throw new Error('expected three registered terminal devices')

  await selectDevice(sshId, 'SSH')
  await page.waitForTimeout(500)
  if (!sshResizeFrames.length) throw new Error('initial terminal size was not sent')
  const initialResize = sshResizeFrames.at(-1)
  const initialResizeCount = sshResizeFrames.length
  await page.setViewportSize({ width: 1500, height: 850 })
  const resizeDeadline = Date.now() + 5000
  while (sshResizeFrames.length === initialResizeCount && Date.now() < resizeDeadline) await page.waitForTimeout(50)
  if (sshResizeFrames.length === initialResizeCount) throw new Error('browser resize did not update terminal size')
  const windowResize = sshResizeFrames.at(-1)
  if (windowResize[0] === initialResize[0] && windowResize[1] === initialResize[1]) throw new Error('terminal dimensions did not change')
  console.log(`SSH_RESIZE_INITIAL=${initialResize.join('x')}`)
  console.log(`SSH_RESIZE_WINDOW=${windowResize.join('x')}`)
  if (process.env.NETCONSOLE_E2E_RESIZE_ONLY === '1') {
    await context.close()
    await browser.close()
    process.exit(0)
  }
  await typeCommand('display interface brief')
  await waitForTerminal('simulated-ssh:display interface brief')
  await waitForTerminal('设备正常')

  await selectDevice(telnetId, 'TELNET')
  await typeCommand('display interface brief')
  await waitForTerminal('simulated-telnet:display interface brief')

  if (process.env.NETCONSOLE_E2E_SKIP_AUTO !== '1') {
    const autoSession = await selectDevice(autoId, 'AUTO')
    const autoStatus = await (await context.request.get(`${baseUrl}/api/system/terminal/sessions/${autoSession}`)).json()
    if (autoStatus.protocol !== 'TELNET') throw new Error(`AUTO fallback selected ${autoStatus.protocol}`)
    await typeCommand('display version')
    await waitForVisibleTerminal('simulated-telnet:display version')
    await page.getByRole('button', { name: '断开', exact: true }).click()
  }

  const expectedHosts = process.env.NETCONSOLE_E2E_SKIP_AUTO === '1' ? 2 : 3
  if (await page.locator('.terminal-host').count() !== expectedHosts) throw new Error('terminal hosts must remain mounted for all tabs')
  await page.locator('.terminal-tab').filter({ hasText: 'SSH' }).last().click()
  await waitForVisibleTerminal('simulated-ssh:display interface brief')
  await page.getByRole('button', { name: '重连', exact: true }).click()
  await page.locator('.terminal-status').filter({ hasText: '受限只读' }).waitFor({ timeout: 15000 })
  await typeCommand('display interface brief')
  await page.waitForFunction(() => [...document.querySelectorAll('.terminal-pane')]
    .filter((pane) => getComputedStyle(pane).display !== 'none')
    .some((pane) => (pane.innerText.match(/simulated-ssh:display interface brief/g) || []).length >= 2), { timeout: 15000 })

  await page.getByRole('button', { name: '分屏', exact: true }).click()
  if (await page.locator('.terminal-panes.split .terminal-pane').count() !== 2) throw new Error('split view did not show two panes')
  const terminalSearch = page.locator('.terminal-console__toolbar input[placeholder="搜索终端"]')
  await terminalSearch.fill('synthetic')
  await terminalSearch.press('Enter')

  await page.getByTitle('切换深色主题').click()
  if (await page.locator('html[data-theme="dark"]').count() !== 1) throw new Error('dark theme did not apply')

  // Serial targets are global resources, so exercise them with the seeded SUPERADMIN.
  const serialContext = await browser.newContext()
  const serialPage = await serialContext.newPage()
  await serialPage.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await serialPage.getByLabel('用户名').fill('superadmin')
  await serialPage.getByLabel('密码').fill('superadmin')
  await serialPage.getByRole('button', { name: '登录工作台' }).click()
  await serialPage.getByRole('heading', { name: '运行总览' }).waitFor()
  await serialPage.locator('.app-sidebar').getByRole('button', { name: '终端控制台' }).click()
  await serialPage.locator('.terminal-console__toolbar').getByRole('button', { name: 'COM', exact: true }).click()
  const serialDialog = serialPage.locator('.el-dialog:visible').filter({ hasText: '打开本地 COM Console' })
  await serialDialog.waitFor()
  await serialDialog.locator('select').selectOption('SIMULATED-COM1')
  await serialDialog.getByRole('button', { name: '打开', exact: true }).click()
  await serialPage.locator('.terminal-tab').filter({ hasText: 'SIMULATED-COM1' }).waitFor()
  await serialPage.locator('.xterm-rows').filter({ hasText: 'simulated console ready' }).waitFor({ timeout: 15000 })
  await serialPage.getByRole('button', { name: '断开', exact: true }).click()
  await serialContext.close()

  invalidTicketProbe = true
  const invalidTicket = await page.evaluate((url) => new Promise((resolve) => {
    const socket = new WebSocket(url)
    const timer = setTimeout(() => { socket.close(); resolve('timeout') }, 3000)
    socket.onopen = () => { clearTimeout(timer); socket.close(); resolve('opened') }
    socket.onerror = () => { clearTimeout(timer); resolve('rejected') }
    socket.onclose = () => { clearTimeout(timer); resolve('rejected') }
  }), `${baseUrl.replace('http', 'ws')}/api/system/terminal/sessions/missing/stream?ticket=invalid`)
  invalidTicketProbe = false
  if (invalidTicket !== 'rejected') throw new Error(`invalid ticket was ${invalidTicket}`)

  await page.getByRole('button', { name: '断开', exact: true }).click()
  await page.getByTestId('terminal-history-row').first().waitFor()
  await page.reload({ waitUntil: 'networkidle' })
  await page.getByTestId('terminal-history-row').first().waitFor()

  const historyRow = page.getByTestId('terminal-history-row').filter({ hasText: 'SSH' }).filter({ hasText: 'COMPLETE' }).first()
  await historyRow.waitFor()
  await historyRow.getByRole('button', { name: '回放 / 导出', exact: true }).click()
  const historyDialog = page.locator('.el-dialog:visible').filter({ hasText: '会话历史 / 只读回放' })
  await historyDialog.waitFor()
  await historyDialog.getByRole('button', { name: '重新开始', exact: true }).click()
  await historyDialog.getByRole('button', { name: '播放', exact: true }).click()
  const downloadPromise = page.waitForEvent('download')
  await historyDialog.getByRole('button', { name: 'JSONL', exact: true }).click()
  const download = await downloadPromise
  if (!(await download.suggestedFilename()).endsWith('.jsonl')) throw new Error('JSONL export filename is wrong')

  if (consoleErrors.length || failedRequests.length || badResponses.length || apiErrors.length) {
    throw new Error(`browser diagnostics: console=${consoleErrors.join(' | ')} failed=${failedRequests.join(' | ')} bad=${badResponses.join(' | ')} api=${apiErrors.join(' | ')}`)
  }
  console.log(`PASS: Vue terminal scoped SSH/Telnet${process.env.NETCONSOLE_E2E_SKIP_AUTO === '1' ? ' (AUTO SKIPPED)' : '/AUTO fallback'}, Unicode output, global COM, split/search/theme, ticket rejection, session close/history/replay/JSONL export`)
} catch (error) {
  const sessionDiagnostics = await Promise.all(sessionIds.map(async (id) => {
    const status = await context.request.get(`${baseUrl}/api/system/terminal/sessions/${id}`)
    const events = await context.request.get(`${baseUrl}/api/system/terminal/sessions/${id}/events`)
    return { status: await status.json(), events: await events.json() }
  })).catch(() => [])
  const diagnostics = await page.evaluate(async () => ({
    url: location.href,
    notice: document.querySelector('.el-message')?.textContent || '',
    tabs: document.querySelector('.terminal-tabs')?.innerText || '',
    terminals: document.querySelector('.terminal-console')?.innerText || '',
    sessions: document.querySelector('.terminal-history-body')?.innerText || '',
  })).catch(() => ({}))
  console.error(`E2E diagnostics: ${JSON.stringify({ error: String(error), diagnostics, sessionDiagnostics, consoleErrors, failedRequests, badResponses, apiErrors })}`)
  throw error
} finally {
  await context.close()
  await browser.close()
}
