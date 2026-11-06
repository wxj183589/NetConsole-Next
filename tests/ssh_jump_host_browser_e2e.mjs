// Settings browser acceptance with mocked HTTP responses. SSH routing is covered
// separately by ssh_jump_host_e2e.py against real loopback SSH simulators.
import assert from 'node:assert/strict'
import { chromium } from '../web/node_modules/playwright/index.mjs'

const base = process.env.NETCONSOLE_E2E_UI_BASE || 'http://127.0.0.1:5173'
const browser = await chromium.launch({ headless: true,
  executablePath: process.env.NETCONSOLE_E2E_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe' })
let role = 'SUPERADMIN'
const writes = []
const states = Object.fromEntries(['site-a', 'site-b'].map((siteId, index) => [siteId, {
  siteId, host: `192.0.2.${index + 1}`, port: 2222, username: `jump-${index + 1}`,
  enabled: true, credentialConfigured: true, hostKeySha256: `SHA256:fixture-${index + 1}`,
  runtimeStatus: 'ON_DEMAND', updatedMs: 1,
}]))
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
const errors = []
context.on('page', page => page.on('pageerror', error => errors.push(error.message)))
await context.route('**/api/**', async route => {
  const request = route.request()
  const path = new URL(request.url()).pathname
  // Vite serves /src/api/client.ts through the same glob.
  if (!path.startsWith('/api/')) return route.continue()
  let result
  if (path === '/api/auth/me') result = { username: 'tester', role }
  else if (path === '/api/auth/csrf') result = { csrfToken: 'synthetic-csrf' }
  else if (path === '/api/system/modules') result = []
  else if (path === '/api/system/health') result = { healthState: 'HEALTHY', collectionState: 'running' }
  else if (path === '/api/system/devices') result = { items: [], summary: { totalRecords: 0 }, total: 0 }
  else if (path === '/api/system/device-sites') result = [{ id: 'site-a', name: 'Site A' }, { id: 'site-b', name: 'Site B' }]
  else if (path.includes('/ssh-jump-host')) {
    const site = path.split('/')[4]
    const state = states[site]
    assert.ok(state, `unexpected Site ${site}`)
    if (request.method() === 'PUT') {
      const payload = request.postDataJSON()
      writes.push({ site, payload })
      Object.assign(state, { host: payload.host, port: payload.port, username: payload.username, enabled: payload.enabled })
      if (payload.clearPassword) state.credentialConfigured = false
      else if (payload.password) state.credentialConfigured = true
    }
    if (path.endsWith('/fingerprint')) state.hostKeySha256 = ''
    else if (path.endsWith('/test') || path.endsWith('/refresh-fingerprint')) state.hostKeySha256 = 'SHA256:authenticated-fixture'
    state.runtimeStatus = !state.enabled ? 'DISABLED' : state.credentialConfigured ? 'ON_DEMAND' : 'CREDENTIAL_MISSING'
    result = state
  } else throw new Error(`Unexpected API request: ${request.method()} ${path}`)
  await route.fulfill({ json: result })
})
const page = await context.newPage()
try {
  await page.goto(base + '/settings')
  const panel = page.getByTestId('site-ssh-jump-host')
  const password = panel.locator('input[type=password]')
  await panel.getByText('192.0.2.1:2222', { exact: true }).waitFor()
  await panel.getByRole('button', { name: '保存设置', exact: true }).click()
  await panel.getByText('设置已保存，自动用于后续网络设备 SSH 连接。').waitFor()
  assert.equal(writes.at(-1).payload.password, undefined)
  assert.equal(writes.at(-1).payload.clearPassword, false)
  await password.fill('discard-when-clearing')
  await panel.getByLabel('明确清除已保存密码').check()
  assert.ok(await password.isDisabled())
  await panel.getByRole('button', { name: '保存设置', exact: true }).click()
  await panel.getByText('缺少密码', { exact: true }).waitFor()
  assert.equal(writes.at(-1).payload.password, undefined)
  assert.equal(writes.at(-1).payload.clearPassword, true)
  assert.equal(await password.inputValue(), '')
  assert.ok(await panel.getByRole('button', { name: '测试连接', exact: true }).isDisabled())
  await password.fill('synthetic-replacement')
  await panel.getByRole('button', { name: '保存设置', exact: true }).click()
  await panel.getByText('密码已保存', { exact: true }).waitFor()
  assert.equal(await password.inputValue(), '')
  for (const name of ['测试连接', '重新获取指纹', '删除指纹']) {
    await panel.getByRole('button', { name, exact: true }).click()
    await panel.getByRole('status').waitFor()
  }
  await panel.getByText('未记录', { exact: true }).waitFor()
  assert.ok(await panel.getByText('密码已保存', { exact: true }).isVisible())
  await password.fill('must-clear-on-site-switch')
  await panel.getByLabel('SSH 中转局点').selectOption('site-b')
  await panel.getByText('192.0.2.2:2222', { exact: true }).waitFor()
  assert.equal(await password.inputValue(), '')
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { document.documentElement.dataset.theme = value }, theme)
    await panel.scrollIntoViewIfNeeded()
    assert.ok(await panel.getByRole('button', { name: '保存设置', exact: true }).isVisible())
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `horizontal overflow in ${theme}`)
  }
  role = 'ADMIN'
  await page.reload()
  await panel.getByText('192.0.2.1:2222', { exact: true }).waitFor()
  assert.equal(await panel.getByRole('button', { name: '保存设置', exact: true }).count(), 0)
  assert.equal(await panel.getByRole('button', { name: '删除指纹', exact: true }).count(), 0)
  assert.deepEqual(errors, [])
  console.log('SITE_JUMP_SETTINGS_BROWSER=PASS (real Chrome, blank/clear, secret reset, probe, key actions, Site switching, roles, light/dark, no overflow/page errors; mocked API)')
} finally {
  await browser.close()
}
