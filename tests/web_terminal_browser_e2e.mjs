import { chromium } from '../web/node_modules/playwright/index.mjs'

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL
const sshPort = process.env.NETCONSOLE_E2E_SSH_PORT
const telnetPort = process.env.NETCONSOLE_E2E_TELNET_PORT
if (!baseUrl || !sshPort || !telnetPort) throw new Error('E2E base URL and simulator ports are required')
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] })
const context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] })
const page = await context.newPage()
const consoleErrors = []
page.on('console', (message) => { if (message.type() === 'error' && !message.text().includes('401') && !message.text().includes('/sessions/missing/stream?ticket=invalid')) consoleErrors.push(message.text()) })
page.on('request', (request) => { if (request.url().includes('cdn.jsdelivr.net')) throw new Error(`CDN request: ${request.url()}`) })

const waitForTerminal = (value) => page.waitForFunction((text) => [...document.querySelectorAll('.xterm-rows')].some((rows) => rows.innerText.includes(text)), value, { timeout: 15000 })
try {
  await page.goto(`${baseUrl}/`, { waitUntil: 'networkidle' })
  await page.locator('input[autocomplete="username"]').fill('admin')
  await page.locator('input[autocomplete="current-password"]').fill('admin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  const sshDevice = page.locator('.device-list__item').filter({ hasText: `:${sshPort}` })
  const telnetDevice = page.locator('.device-list__item').filter({ hasText: `:${telnetPort}` })
  await page.locator('.nav-item').filter({ hasText: '终端控制台' }).click()
  await sshDevice.getByRole('button', { name: 'SSH' }).click()
  await waitForTerminal('SSH synthetic ready')
  await page.locator('.terminal-pane:visible').first().locator('.xterm-helper-textarea').focus()
  await page.keyboard.insertText('display interface brief')
  await page.keyboard.press('Enter')
  await waitForTerminal('simulated-ssh:display interface brief')
  await telnetDevice.getByRole('button', { name: 'TELNET' }).click()
  await waitForTerminal('TELNET synthetic ready')
  await page.getByRole('button', { name: '分屏' }).click()
  if (await page.locator('.terminal-panes.split .terminal-pane').count() !== 2) throw new Error('split view did not show two panes')
  await page.locator('.terminal-toolbar input[placeholder*="搜索"]').fill('synthetic')
  await page.locator('.terminal-toolbar input[placeholder*="搜索"]').press('Enter')
  await page.getByTitle('切换深色主题').click()
  if (await page.locator('html[data-theme="dark"]').count() !== 1) throw new Error('dark theme did not apply')
  await page.getByRole('button', { name: '打开 COM' }).click()
  await page.locator('.el-dialog').getByText('端口').waitFor()
  await page.locator('.el-dialog select').selectOption('SIMULATED-COM1')
  await page.locator('.el-dialog').getByRole('button', { name: '打开' }).click()
  await waitForTerminal('simulated console ready')
  const unauthenticated = await context.browser().newContext().then(async (unauthContext) => {
    const response = await unauthContext.request.get(`${baseUrl}/api/system/terminal/devices`)
    await unauthContext.close()
    return response.status()
  })
  if (unauthenticated !== 401) throw new Error(`unauthenticated API status was ${unauthenticated}`)
  const invalidTicket = await page.evaluate((url) => new Promise((resolve) => {
    const socket = new WebSocket(url); const timer = setTimeout(() => { socket.close(); resolve('timeout') }, 3000)
    socket.onerror = () => { clearTimeout(timer); resolve('rejected') }; socket.onclose = () => { clearTimeout(timer); resolve('rejected') }
  }), `${baseUrl.replace('http', 'ws')}/api/system/terminal/sessions/missing/stream?ticket=invalid`)
  if (invalidTicket !== 'rejected') throw new Error(`invalid ticket was ${invalidTicket}`)
  await page.getByRole('button', { name: '断开' }).click()
  await page.reload({ waitUntil: 'networkidle' })
  await page.locator('.nav-item').filter({ hasText: '终端控制台' }).click()
  await page.locator('.history-item').first().waitFor()
  await page.locator('.history-item').first().click()
  await page.getByText('会话历史 / 只读回放').waitFor()
  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'JSONL' }).click()
  const download = await downloadPromise
  if (!(await download.suggestedFilename()).endsWith('.jsonl')) throw new Error('JSONL export filename is wrong')
  if (consoleErrors.length) throw new Error(`browser diagnostics: ${consoleErrors.join(' | ')}`)
  console.log('PASS: Vue terminal login, SSH/Telnet/COM, split/search/theme, cookie auth, ticket rejection, history/replay/export')
} finally {
  await context.close(); await browser.close()
}

