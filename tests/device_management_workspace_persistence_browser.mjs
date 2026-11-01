import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { chromium } from '../web/node_modules/playwright/index.mjs'
import { workspacePath } from '../tools/workspace_paths.mjs'

const base = process.env.NETCONSOLE_E2E_BASE_URL || 'http://127.0.0.1:18883'
const dataRoot = process.env.NETCONSOLE_E2E_DATA_ROOT
if (!dataRoot) throw new Error('NETCONSOLE_E2E_DATA_ROOT must point to the isolated browser data root')
const output = workspacePath('build', process.env.NETCONSOLE_DEVICE_WORKSPACE_OUTPUT || 'device-management-workspace-browser')
mkdirSync(output, { recursive: true })
const token = readFileSync(join(dataRoot, 'local-api-token'), 'utf8').trim()

async function api(path) {
  const response = await fetch(base + path, { headers: { Authorization: `Bearer ${token}` } })
  const text = await response.text()
  if (!response.ok) throw new Error(`GET ${path} -> ${response.status}: ${text}`)
  return JSON.parse(text)
}

const pageData = await api('/api/system/devices?page=1&pageSize=100&siteId=local')
if (!pageData.items.some(item => item.id === 'workspace-local-01')) throw new Error('local fixture did not survive service restart')
const evidence = { passed: false, checks: ['catalog query after service restart returned persisted local device'], screenshots: [] }
const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', args: ['--no-sandbox'] })
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 })
const page = await context.newPage()
try {
  await page.goto(`${base}/login`, { waitUntil: 'networkidle' })
  await page.locator('input[autocomplete="username"]').fill('superadmin')
  await page.locator('input[autocomplete="current-password"]').fill('superadmin')
  await page.getByRole('button', { name: '登录工作台' }).click()
  await page.getByRole('heading', { name: '运行总览' }).waitFor()
  await page.goto(`${base}/devices`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: '设备管理' }).waitFor()
  await page.locator('tbody tr').filter({ hasText: '工作台 Local 01' }).waitFor()
  evidence.checks.push('real browser query after restart returned persisted local row')
  await page.locator('.device-tree-node').filter({ hasText: '全部授权 Site' }).click()
  await page.locator('tbody tr').filter({ hasText: '工作台 Site B 13' }).waitFor()
  evidence.checks.push('real browser All Sites query after restart returned persisted Site B row')
  await page.locator('tbody tr').filter({ hasText: '工作台 Local 12' }).first().getByRole('button', { name: '详情' }).click()
  await page.getByRole('dialog', { name: '设备详情 / 审计' }).waitFor()
  const path = join(output, 'device-workspace-after-restart.png')
  await page.screenshot({ path, fullPage: true })
  evidence.screenshots.push({ name: 'device-workspace-after-restart.png', path, viewport: page.viewportSize() })
  evidence.passed = true
  console.log('PASS: service restart persistence query and real browser detail lookup')
} finally {
  writeFileSync(join(output, 'persistence-evidence.json'), JSON.stringify(evidence, null, 2), 'utf8')
  await context.close()
  await browser.close()
}
if (!evidence.passed) process.exitCode = 1
