import { chromium } from '../web/node_modules/playwright/index.mjs'

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL
const sshPort = process.env.NETCONSOLE_E2E_SSH_PORT
const telnetPort = process.env.NETCONSOLE_E2E_TELNET_PORT
if (!baseUrl || !sshPort || !telnetPort) throw new Error('E2E base URL and simulator ports are required')

const executablePath = process.env.NETCONSOLE_E2E_BROWSER
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ['--no-sandbox'] })
const context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] })
const page = await context.newPage()
const consoleErrors = []
const failedRequests = []
const badResponses = []
const apiErrors = []
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
  await page.getByTestId('terminal-open-button').click()
  await page.locator('.terminal-tab').filter({ hasText: protocol }).last().waitFor()
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
  await page.getByTestId('terminal-device-row').nth(1).waitFor()
  if (await page.getByTestId('terminal-device-row').count() !== 2) throw new Error('expected two registered terminal devices')

  await selectDevice(sshId, 'SSH')
  await waitForTerminal('SSH synthetic ready')
  await typeCommand('display interface brief')
  await waitForTerminal('simulated-ssh:display interface brief')

  await selectDevice(telnetId, 'TELNET')
  await waitForTerminal('TELNET synthetic ready')
  await typeCommand('display interface brief')
  await waitForTerminal('simulated-telnet:')

  if (await page.locator('.terminal-host').count() !== 2) throw new Error('terminal hosts must remain mounted when two tabs are open')
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

  await page.locator('.terminal-console__toolbar').getByRole('button', { name: 'COM', exact: true }).click()
  const serialDialog = page.locator('.el-dialog:visible').filter({ hasText: '打开本地 COM Console' })
  await serialDialog.waitFor()
  await serialDialog.locator('select').selectOption('SIMULATED-COM1')
  await serialDialog.getByRole('button', { name: '打开', exact: true }).click()
  await page.locator('.terminal-tab').filter({ hasText: 'SIMULATED-COM1' }).waitFor()
  await waitForTerminal('simulated console ready')

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

  const historyRow = page.getByTestId('terminal-history-row').first()
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
  console.log('PASS: Vue terminal device selection, SSH/Telnet/COM, split/search/theme, ticket rejection, session close/history/replay/JSONL export')
} catch (error) {
  const diagnostics = await page.evaluate(async () => ({
    url: location.href,
    notice: document.querySelector('.el-message')?.textContent || '',
    tabs: document.querySelector('.terminal-tabs')?.innerText || '',
    terminals: document.querySelector('.terminal-console')?.innerText || '',
    sessions: document.querySelector('.terminal-history-body')?.innerText || '',
  })).catch(() => ({}))
  console.error(`E2E diagnostics: ${JSON.stringify({ error: String(error), diagnostics, consoleErrors, failedRequests, badResponses, apiErrors })}`)
  throw error
} finally {
  await context.close()
  await browser.close()
}
