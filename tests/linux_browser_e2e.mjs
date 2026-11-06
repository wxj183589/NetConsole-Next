import { chromium } from '../web/node_modules/playwright/index.mjs'
import { readFile } from 'node:fs/promises'
import { join } from 'node:path'

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL
const serverId = process.env.NETCONSOLE_E2E_LINUX_SERVER_ID
if (!baseUrl || !serverId) throw new Error('Linux E2E base URL and seeded server ID are required')
const output = process.env.NETCONSOLE_E2E_LINUX_OUTPUT || 'Linux 命令完成 设备正常'
const command = process.env.NETCONSOLE_E2E_LINUX_COMMAND || 'echo netconsole-next-linux-browser-中文'
const executablePath = process.env.NETCONSOLE_E2E_BROWSER
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ['--no-sandbox', '--disable-features=LocalNetworkAccessChecks'] })
const context = await browser.newContext({ viewport: { width: 1360, height: 900 } })
if (process.env.NETCONSOLE_E2E_WEBROOT) {
  await context.route('**/*', async route => {
    const url = new URL(route.request().url())
    if (url.origin !== baseUrl || route.request().method() !== 'GET') return route.continue()
    const path = url.pathname.startsWith('/app/assets/') ? url.pathname.slice(1) :
      ['/', '/login', '/linux', '/terminal'].includes(url.pathname) ? 'index.html' : null
    if (!path || path.includes('..')) return route.continue()
    try {
      const body = await readFile(join(process.env.NETCONSOLE_E2E_WEBROOT, path))
      await route.fulfill({ status: 200, contentType: path.endsWith('.js') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html', body })
    } catch { await route.continue() }
  })
}
const page = await context.newPage()
const errors = []
const sentFrames = []
const socketEvents = []
const sessionIds = new Set()
let cloneId
let socketCount = 0
page.on('pageerror', error => errors.push(error.message))
page.on('response', response => {
  if (response.url().includes('/api/') && response.status() >= 400 &&
      !(response.status() === 401 && response.url().endsWith('/api/auth/me'))) errors.push(`${response.status()} ${new URL(response.url()).pathname}`)
})
page.on('requestfailed', request => {
  if (!request.failure()?.errorText.includes('ERR_ABORTED')) errors.push(`${new URL(request.url()).pathname}: ${request.failure()?.errorText}`)
})
page.on('request', request => { if (request.url().includes('cdn.jsdelivr.net')) errors.push('Unexpected CDN request') })
page.on('websocket', socket => {
  if (!socket.url().includes('/api/system/terminal/sessions/')) return
  const id = ++socketCount
  socketEvents.push({ id, type: 'open' })
  socket.on('close', () => socketEvents.push({ id, type: 'close' }))
  socket.on('socketerror', error => errors.push(`Terminal WebSocket: ${error}`))
  socket.on('framesent', event => {
    try { sentFrames.push({ id, ...JSON.parse(event.payload) }) } catch { /* only JSON control frames are sent */ }
  })
})
async function eventually(check, label) {
  const deadline = Date.now() + 15000
  while (Date.now() < deadline) {
    if (await check()) return
    await page.waitForTimeout(50)
  }
  throw new Error(label)
}
async function api(path, method = 'GET', data) {
  const csrf = method === 'GET' ? undefined : (await (await context.request.get(`${baseUrl}/api/auth/csrf`)).json()).csrfToken
  const response = await context.request.fetch(`${baseUrl}${path}`, { method, ...(data === undefined ? {} : { data }), headers: { Origin: baseUrl, ...(csrf ? { 'X-NC-CSRF': csrf } : {}) } })
  if (!response.ok()) throw new Error(`${method} ${path}: ${response.status()}`)
  return response.json()
}
const visiblePane = () => page.locator('.terminal-pane:visible').last()
async function waitForOutput(text, count = 1) {
  await page.waitForFunction(({ text, count }) => [...document.querySelectorAll('.terminal-pane')]
    .filter(pane => getComputedStyle(pane).display !== 'none')
    .some(pane => pane.innerText.split(text).length - 1 >= count), { text, count }, { timeout: 15000 })
}
async function typeCommand(text) {
  await visiblePane().locator('.xterm-helper-textarea').focus()
  await page.keyboard.insertText(text)
  await page.keyboard.press('Enter')
}
async function readySession(responsePromise) {
  const response = await responsePromise
  if (response.status() !== 201) throw new Error(`Linux terminal creation: ${response.status()}`)
  const { sessionId } = await response.json()
  sessionIds.add(sessionId)
  await eventually(async () => (await api(`/api/system/terminal/sessions/${sessionId}`)).state === 'CONNECTED', 'Linux SSH did not connect')
  await page.locator('.terminal-status').filter({ hasText: 'Linux 交互' }).waitFor()
  return sessionId
}
const sessionCreated = () => page.waitForResponse(response => response.url().includes('/api/system/linux/servers/') && response.url().endsWith('/sessions') && response.request().method() === 'POST')

try {
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' })
  await page.getByLabel('用户名', { exact: true }).fill('superadmin')
  await page.getByLabel('密码', { exact: true }).fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  await page.locator('.app-sidebar').getByRole('button', { name: 'Linux 服务器', exact: true }).click()
  const seed = (await api('/api/system/linux/servers')).find(server => server.id === serverId)
  if (!seed) throw new Error('Seed Linux server is missing')
  const cloneName = `Linux 浏览器 ${Date.now()}`
  await page.getByRole('button', { name: '添加服务器', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '添加 Linux 服务器', exact: true })
  await dialog.getByLabel('名称', { exact: true }).fill(cloneName)
  await dialog.getByLabel('服务器地址', { exact: true }).fill(seed.host)
  await dialog.getByLabel('SSH 端口', { exact: true }).fill(String(seed.port))
  await dialog.getByLabel('用户名', { exact: true }).fill(seed.username)
  await dialog.getByLabel('密码', { exact: true }).fill(process.env.NETCONSOLE_E2E_LINUX_PASSWORD || 'synthetic-linux-password')
  await dialog.getByLabel('分组', { exact: true }).fill('Browser E2E')
  await dialog.locator('select').selectOption(seed.siteId)
  const saved = page.waitForResponse(response => response.url().endsWith('/api/system/linux/servers') && response.request().method() === 'POST')
  await dialog.getByRole('button', { name: '保存服务器', exact: true }).click()
  const savedResponse = await saved
  if (savedResponse.status() !== 201) throw new Error(`Linux UI create: ${savedResponse.status()}`)
  cloneId = (await savedResponse.json()).id
  await dialog.waitFor({ state: 'hidden' })
  await page.getByLabel('搜索服务器', { exact: true }).fill(cloneName)
  const cloneRow = page.locator(`tr[data-server-id="${cloneId}"]`)
  await cloneRow.waitFor()
  if (await page.locator('tbody tr[data-server-id]').count() !== 1) throw new Error('Linux search did not filter the server list')
  await cloneRow.getByRole('button', { name: '编辑', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '编辑 Linux 服务器', exact: true })
  if (await dialog.getByLabel('更新密码').inputValue()) throw new Error('Linux edit returned a saved password')
  await dialog.getByLabel('分组', { exact: true }).fill('Browser edited')
  const updated = page.waitForResponse(response => response.url().endsWith(`/api/system/linux/servers/${cloneId}`) && response.request().method() === 'PUT')
  await dialog.getByRole('button', { name: '保存服务器', exact: true }).click()
  if (!(await updated).ok()) throw new Error('Linux UI edit failed')
  await dialog.waitFor({ state: 'hidden' })
  const tested = page.waitForResponse(response => response.url().endsWith(`/api/system/linux/servers/${cloneId}/test`) && response.request().method() === 'POST')
  await cloneRow.getByRole('button', { name: '测试连接', exact: true }).click()
  if ((await (await tested).json()).status !== 'CONNECTED') throw new Error('Blank password edit failed to preserve automatic login')
  await page.getByLabel('搜索服务器', { exact: true }).fill('')
  const firstCreated = sessionCreated()
  await page.locator(`tr[data-server-id="${serverId}"]`).getByRole('button', { name: '打开终端', exact: true }).click()
  const firstId = await readySession(firstCreated)
  await waitForOutput('linux-simulator 已连接')
  await typeCommand(command)
  await waitForOutput(output)
  await visiblePane().locator('.xterm-helper-textarea').focus()
  await page.keyboard.press('Control+c')
  await waitForOutput('^C')
  await page.keyboard.press('Tab')
  await waitForOutput('completion')
  await page.keyboard.press('ArrowUp')
  await eventually(() => {
    const raw = sentFrames.filter(frame => frame.type === 'input').map(frame => frame.data || '').join('')
    return raw.includes(command) && raw.includes('\r') && raw.includes('\x03') && raw.includes('\t') && raw.includes('\x1b[A')
  }, 'Linux xterm did not forward raw Unicode and control keys')
  if (sentFrames.some(frame => frame.type === 'input' && Object.hasOwn(frame, 'text'))) throw new Error('Linux keyboard was sent through the network command input field')
  const initialSize = sentFrames.filter(frame => frame.type === 'resize').at(-1)
  const beforeResize = sentFrames.filter(frame => frame.type === 'resize').length
  await page.setViewportSize({ width: 1580, height: 1000 })
  await eventually(() => sentFrames.filter(frame => frame.type === 'resize').length > beforeResize, 'Linux terminal did not resize')
  const newSize = sentFrames.filter(frame => frame.type === 'resize').at(-1)
  if (initialSize.columns === newSize.columns && initialSize.rows === newSize.rows) throw new Error('Linux terminal dimensions stayed unchanged')

  const secondCreated = sessionCreated()
  await page.getByTestId('linux-terminal-target').filter({ hasText: cloneName }).click()
  await readySession(secondCreated)
  await waitForOutput('linux-simulator 已连接')
  if (await page.locator('.terminal-tab').count() !== 2) throw new Error('Linux terminals did not create independent tabs')
  await typeCommand('echo second-linux-tab')
  await waitForOutput(output)
  await page.locator('.terminal-tab').filter({ hasText: seed.name }).click()
  await waitForOutput('completion')
  const socketsBefore = socketCount
  await page.getByRole('button', { name: '重连', exact: true }).click()
  await eventually(() => socketCount > socketsBefore, 'Linux WebSocket reconnect did not open a new socket')
  await page.locator('.terminal-status').filter({ hasText: 'Linux 交互' }).waitFor()
  const outputCount = await visiblePane().evaluate((pane, needle) => pane.innerText.split(needle).length - 1, output)
  await typeCommand('echo websocket-reconnected')
  await waitForOutput(output, outputCount + 1)
  await api(`/api/system/terminal/sessions/${firstId}/close`, 'POST', {})
  await eventually(async () => (await api(`/api/system/terminal/sessions/${firstId}`)).state === 'CLOSED', 'Linux session did not close')
  await page.locator('.terminal-status').filter({ hasText: '未连接' }).waitFor()
  const recreated = sessionCreated()
  await page.getByRole('button', { name: '重连', exact: true }).click()
  const recreatedId = await readySession(recreated)
  if (recreatedId === firstId) throw new Error('Linux reconnect reused a closed session')
  const beforeSshCommand = await visiblePane().evaluate((pane, needle) => pane.innerText.split(needle).length - 1, output)
  await typeCommand('echo ssh-session-reconnected')
  await waitForOutput(output, beforeSshCommand + 1)
  await page.getByRole('button', { name: '分屏', exact: true }).click()
  if (await page.locator('.terminal-panes.split .terminal-pane:visible').count() !== 2) throw new Error('Linux split did not show two terminal panes')
  if (sentFrames.filter(frame => frame.type === 'resize').some(frame => frame.columns < 1 || frame.columns > 500 || frame.rows < 1 || frame.rows > 300)) throw new Error('Linux terminal produced an invalid PTY size')
  if (await page.locator('.terminal-console').evaluate(element => element.getBoundingClientRect().height) > 1000) throw new Error('Linux terminal layout grew beyond the viewport')

  while (await page.locator('.terminal-tab').count()) {
    const remaining = await page.locator('.terminal-tab').count() - 1
    await page.getByRole('button', { name: '断开', exact: true }).click()
    await eventually(async () => await page.locator('.terminal-tab').count() === remaining, 'Linux tab did not close')
  }
  await page.locator('.app-sidebar').getByRole('button', { name: 'Linux 服务器', exact: true }).click()
  await cloneRow.getByRole('button', { name: '删除', exact: true }).click()
  const deleteDialog = page.getByRole('dialog', { name: '删除 Linux 服务器', exact: true })
  if (!(await deleteDialog.innerText()).includes(cloneName)) throw new Error('Linux delete dialog omitted the target name')
  const deleted = page.waitForResponse(response => response.url().endsWith(`/api/system/linux/servers/${cloneId}`) && response.request().method() === 'DELETE')
  await deleteDialog.getByRole('button', { name: '确认删除', exact: true }).click()
  if (!(await deleted).ok()) throw new Error('Linux UI deletion failed')
  await cloneRow.waitFor({ state: 'detached' })
  cloneId = undefined
  if (errors.length) throw new Error(`Linux browser errors: ${JSON.stringify(errors)}`)
  console.log('LINUX_BROWSER=PASS (CRUD/search/password retention, ANSI/Unicode, raw Ctrl-C/Tab/arrows, resize, multiple tabs/split, WebSocket and SSH reconnect)')
} catch (error) {
  console.error(`LINUX_BROWSER_DIAGNOSTICS=${JSON.stringify({ errors, socketEvents, frameTypes: sentFrames.map(({ id, type, columns, rows }) => ({ id, type, columns, rows })) })}`)
  throw error
} finally {
  for (const id of sessionIds) await api(`/api/system/terminal/sessions/${id}/close`, 'POST', {}).catch(() => {})
  if (cloneId) await api(`/api/system/linux/servers/${cloneId}`, 'DELETE').catch(() => {})
  await context.close()
  await browser.close()
}
