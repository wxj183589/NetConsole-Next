import { chromium } from '../web/node_modules/playwright/index.mjs'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { workspacePath } from '../tools/workspace_paths.mjs'

// Actual Vue -> authenticated local service. Only stored evidence and target resolution.
// No device sessions, policy enabling, capture, recovery or configuration writes.
const ui = process.env.R72_UI_URL || 'http://127.0.0.1:5174'
const api = process.env.R72_API_URL || 'http://127.0.0.1:18771'
const output = workspacePath('reports', process.env.R72_BROWSER_OUTPUT || 'r72-browser-20261108')
mkdirSync(output, { recursive: true })
const result = { evidence: 'LOCAL_RUNTIME_READ_ONLY', ui, api, startedAt: new Date().toISOString(), checks: {}, apiFailures: [], pageErrors: [], consoleErrors: [], layouts: [] }
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
const parseCsv = text => {
  const rows=[]; let row=[],cell='',quoted=false
  const source=text.replace(/^\uFEFF/,'')
  for(let i=0;i<source.length;i++){
    const ch=source[i]
    if(ch==='"') { if(quoted&&source[i+1]==='"'){cell+='"';i++}else quoted=!quoted }
    else if(ch===','&&!quoted){row.push(cell);cell=''}
    else if(ch==='\r'&&!quoted&&source[i+1]==='\n'){row.push(cell);rows.push(row);row=[];cell='';i++}
    else cell+=ch
  }
  assert(!quoted,'Downloaded CSV has an unterminated quoted cell')
  if(row.length||cell.length){row.push(cell);rows.push(row)}
  assert(rows.every(row=>row.length===rows[0].length),'Downloaded CSV rows must match the header width')
  return rows.slice(1).map(row=>Object.fromEntries(rows[0].map((header,i)=>[header,row[i]])))
}
const read = async url => page.evaluate(async target => {
  const response = await fetch(target, { credentials: 'same-origin' })
  if (!response.ok) throw new Error(`${response.status} ${target}`)
  return response.json()
}, url)
const navigate = async name => {
  await page.locator('.app-sidebar').getByRole('button', { name, exact: true }).click()
  await page.getByRole('heading', { name, exact: true }).waitFor()
}
const resetFaultQuery = async panel => {
  await page.waitForFunction(()=>!document.querySelector('.fault-center button[type=submit]')?.disabled)
  const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults'&&!new URL(r.url()).searchParams.has('deviceId')&&!new URL(r.url()).searchParams.has('state')&&!new URL(r.url()).searchParams.has('kind')&&!new URL(r.url()).searchParams.has('fromMs')&&!new URL(r.url()).searchParams.has('toMs'))
  await panel.getByRole('button',{name:'重置故障筛选',exact:true}).click()
  const result=await(await response).json()
  await page.waitForFunction(()=>!document.querySelector('.fault-center button[type=submit]')?.disabled)
  return result
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
  if (name !== 'login' && process.env.R72_STEPS && !process.env.R72_STEPS.split(',').includes(name)) return
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
    await page.getByLabel('用户名').fill(process.env.R72_USER || 'superadmin')
    await page.getByLabel('密码').fill(process.env.R72_PASSWORD || 'superadmin')
    await page.getByRole('button', { name: '登录工作台' }).click()
    await page.getByRole('heading', { name: '运行总览', exact: true }).waitFor()
    authenticated = true
    return {}
  })
  if (!authenticated) throw new Error('Login failed; business operations skipped')
  await step('dashboardShortcuts', async () => {
    await navigate('运行总览')
    const nav=page.getByRole('navigation',{name:'运维快捷操作'})
    await nav.getByRole('link',{name:'Syslog 查询与导出',exact:true}).click()
    await page.getByRole('heading',{name:'工具集',exact:true}).waitFor()
    await page.locator('.syslog-workspace').waitFor()
    await navigate('运行总览')
    await nav.getByRole('link',{name:'拓扑与资产关系',exact:true}).click()
    await page.getByRole('heading',{name:'拓扑与资产',exact:true}).waitFor()
    await navigate('运行总览')
    await page.waitForFunction(()=>document.querySelectorAll('.stats-grid .el-loading-mask').length===0)
    await page.getByText('正在读取授权设备健康…',{exact:true}).waitFor({state:'hidden'})
    await nav.getByRole('link',{name:'故障历史与恢复证据',exact:true}).click()
    assert(await page.evaluate(()=>document.activeElement?.id==='dashboard-fault-center'),'Fault shortcut must focus current-page history')
    await screenshots('dashboard-shortcut-fault-focus')
    return {syslog:'/tools',topology:'/topology',fault:'#dashboard-fault-center',deviceConnections:0}
  })
  await step('faultExport', async () => {
    await navigate('运行总览')
    const panel=page.getByRole('region',{name:'持久故障中心'})
    const baseline=await resetFaultQuery(panel)
    assert(baseline.items.length>0,'Fault CSV acceptance requires a stored event')
    const selected=baseline.items[0]
    await panel.getByLabel('设备 ID',{exact:true}).fill(selected.deviceId)
    const pending=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults'&&new URL(r.url()).searchParams.get('deviceId')===selected.deviceId)
    await panel.getByRole('button',{name:'查询故障',exact:true}).click()
    const loaded=await (await pending).json()
    await page.waitForFunction(()=>!document.querySelector('.fault-center button[type=submit]')?.disabled)
    await panel.getByLabel('设备 ID',{exact:true}).fill('R72_EDITED_UNSUBMITTED_FILTER')
    const requests=[]
    const observe=request=>{if(new URL(request.url()).pathname==='/api/system/operations/faults')requests.push(request.url())}
    page.on('request',observe)
    const downloading=page.waitForEvent('download')
    await panel.getByRole('button',{name:'导出当前页故障与历史 CSV',exact:true}).click()
    const file=await downloading;const target=path.join(output,'fault-current-page.csv');await file.saveAs(target)
    page.off('request',observe)
    const bytes=readFileSync(target)
    assert(bytes[0]===0xef&&bytes[1]===0xbb&&bytes[2]===0xbf,'Downloaded UTF-8 CSV must retain its BOM')
    const csv=bytes.toString('utf8');const rows=parseCsv(csv)
    const events=rows.filter(row=>row.record_type==='EVENT'),history=rows.filter(row=>row.record_type==='HISTORY')
    assert(requests.length===0,'Fault CSV must use loaded page without an extra network query')
    assert(events.length===loaded.items.length,'CSV must contain precisely the loaded-page incidents')
    assert(history.length===loaded.items.reduce((sum,item)=>sum+item.history.length,0),'CSV must retain all current-page stored transition history')
    for(const item of loaded.items){
      const event=events.find(row=>row.fault_id===item.id)
      assert(event&&event.device_id===item.deviceId&&event.profile_id===item.profileId&&event.site_id===item.siteId,'CSV lost exact fault identity')
      assert(event.state===item.state&&event.occurrences===String(item.occurrences)&&event.source_type===item.evidenceType&&event.evidence_id===item.evidenceId,'CSV lost lifecycle/source facts')
      assert(event.first_seen===new Date(item.firstSeenMs).toISOString()&&event.last_seen===new Date(item.lastSeenMs).toISOString(),'CSV lost observed times')
      assert(item.history.every(entry=>history.some(row=>row.fault_id===item.id&&row.history_at===new Date(entry.atMs).toISOString()&&row.history_action===entry.action&&row.history_actor_user_id===entry.actorUserId&&row.history_evidence_id===entry.evidenceId)),'CSV lost history provenance')
    }
    assert(rows.every(row=>row.scope==='CURRENT_PAGE_LOADED_QUERY_ONLY'&&row.page===String(loaded.page)&&row.page_size===String(loaded.pageSize)&&row.total_matching===String(loaded.total)&&row.filter_device_id===selected.deviceId),'CSV scope must describe loaded filter/page')
    assert(!csv.includes('R72_EDITED_UNSUBMITTED_FILTER'),'Unsubmitted draft must not relabel exported facts')
    await screenshots('fault-csv-current-page')
    const emptyPending=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults'&&new URL(r.url()).searchParams.get('deviceId')==='R72_EDITED_UNSUBMITTED_FILTER')
    await panel.getByRole('button',{name:'查询故障',exact:true}).click()
    const empty=await(await emptyPending).json()
    await panel.getByText('当前筛选没有故障记录。',{exact:true}).waitFor()
    assert(empty.items.length===0&&await panel.getByRole('button',{name:'导出当前页故障与历史 CSV',exact:true}).isDisabled(),'Empty current query must disable CSV')
    return {filename:file.suggestedFilename(),path:target,bytes:Buffer.byteLength(csv),page:loaded.page,total:loaded.total,events:events.length,history:history.length,deviceId:selected.deviceId,extraExportQueries:requests.length,deviceConnections:0,policyChanges:0}
  })
  await step('faultCenter', async () => {
    await navigate('运行总览')
    const panel = page.getByRole('region', { name: '持久故障中心' })
    await panel.getByRole('heading', { name: '故障中心', exact: true }).waitFor()
    await resetFaultQuery(panel)
    const sync = page.waitForResponse(r => new URL(r.url()).pathname === '/api/system/operations/faults/sync' && r.request().method() === 'POST')
    await panel.getByRole('button', { name: '同步当前证据', exact: true }).click()
    assert((await sync).ok(), 'Local projection sync failed')
    await panel.getByRole('button', { name: '刷新故障历史', exact: true }).waitFor({ state: 'visible' })
    const baseline = await read('/api/system/operations/faults?page=1')
    assert(baseline.policies.every(p => !p.enabled), 'Browser acceptance expects automatic policies disabled')
    await panel.getByRole('button', { name: '查询故障', exact: true }).waitFor()
    await page.waitForFunction(() => !document.querySelector('.fault-center button[type=submit]')?.disabled)
    if (baseline.items.length) {
      await panel.getByRole('button', { name: '查看处理历史', exact: true }).first().click()
      assert(await panel.locator('ol li').count() > 0, 'Persisted event lacks processing history')
    }
    await screenshots('fault-center-history')
    const filters=[]
    for (const value of ['OPEN','ACKNOWLEDGED','PROCESSING','RESOLVED','REOPENED']) {
      await panel.getByLabel('故障状态', { exact: true }).selectOption(value)
      const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/system/operations/faults' && new URL(r.url()).searchParams.get('state') === value)
      await panel.getByRole('button', { name: '查询故障', exact: true }).click()
      const data = await (await response).json()
      assert(data.items.every(i=>i.state===value), 'Fault state filter returned unrelated states')
      const summary=panel.getByLabel('筛选范围故障统计',{exact:true})
      await summary.waitFor()
      assert(data.summary && data.summary[value.toLowerCase()]===data.total,'Filtered summary must cover the entire filtered result')
      filters.push({value,total:data.total})
      await page.waitForFunction(() => !document.querySelector('.fault-center button[type=submit]')?.disabled)
    }
    await panel.getByLabel('故障状态', { exact: true }).selectOption('')
    await panel.getByLabel('设备 ID', { exact: true }).fill('r72-browser-no-such-device')
    await panel.getByLabel('故障类型', { exact: true }).selectOption('INTERFACE_DOWN')
    await panel.getByLabel('起始时间', { exact: true }).fill('2026-11-01T00:00')
    await panel.getByLabel('截止时间', { exact: true }).fill('2026-11-30T23:59')
    const emptyResponse=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults' && new URL(r.url()).searchParams.get('deviceId')==='r72-browser-no-such-device')
    await panel.getByRole('button', { name: '查询故障', exact: true }).click()
    const empty=await (await emptyResponse).json()
    assert(empty.total===0, 'Unknown exact device must produce scoped empty results')
    await panel.getByText('当前筛选没有故障记录。',{exact:true}).waitFor()
    await screenshots('fault-center-empty')
    return {baseline,filters,empty, deviceConnections:0,policyChanges:0, localOnlySync:true}
  })
  await step('faultNavigation', async () => {
    await navigate('运行总览')
    const panel=page.getByRole('region',{name:'持久故障中心'})
    const baseline=await resetFaultQuery(panel)
    await panel.getByRole('button',{name:'仅看此设备故障',exact:true}).first().waitFor()
    assert(baseline.items.length>0,'Fault navigation requires actual stored incident')
    const selected=baseline.items[0]
    const filteredResponse=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults'&&new URL(r.url()).searchParams.get('deviceId')===selected.deviceId)
    await panel.getByRole('button',{name:'仅看此设备故障',exact:true}).first().click()
    const filtered=await (await filteredResponse).json()
    assert(filtered.page===1&&filtered.items.every(item=>item.deviceId===selected.deviceId),'Device fault shortcut must retain exact device ID')
    assert(await panel.getByLabel('设备 ID',{exact:true}).inputValue()===selected.deviceId,'Device shortcut must reflect active visible filter')
    await screenshots('fault-navigation-device-filter')
    const resetResponse=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/operations/faults'&&!new URL(r.url()).searchParams.has('deviceId'))
    await panel.getByRole('button',{name:'重置故障筛选',exact:true}).click()
    const reset=await (await resetResponse).json()
    assert(reset.total===baseline.total,'Reset must restore current first-page scoped history')
    const sources=[]
    for(const anchor of await panel.locator('a[target="_blank"]').all()){
      const href=await anchor.getAttribute('href')
      if(!href?.startsWith('/api/system/tasks/'))continue
      const source=await read(href)
      const id=decodeURIComponent(href.split('/').at(-1))
      assert(source.id===id,'Source task shortcut must resolve original task ID')
      sources.push({href,id:source.id,state:source.state})
    }
    const link=panel.getByRole('link',{name:'查看此设备配置历史',exact:true}).first()
    const href=await link.getAttribute('href')
    assert(new URL(href,ui).searchParams.get('deviceId')===selected.deviceId,'Configuration history link must use exact selected incident device ID')
    const detailResponse=page.waitForResponse(r=>new URL(r.url()).pathname===`/api/system/devices/${encodeURIComponent(selected.deviceId)}`)
    await link.click()
    const detail=await (await detailResponse).json()
    await page.getByRole('heading',{name:'配置备份',exact:true}).waitFor()
    assert(detail.item.id===selected.deviceId&&detail.terminalProfileId===selected.profileId,'Configuration route must revalidate exact current device/profile association')
    await page.getByRole('heading',{name:new RegExp('版本历史')}).waitFor()
    await screenshots('fault-navigation-configuration-history')
    return {selectedFault:selected.id,deviceId:selected.deviceId,profileId:selected.profileId,filteredTotal:filtered.total,resetTotal:reset.total,sources,configurationHref:href,deviceConnections:0,policyChanges:0}
  })
  await step('completePreview', async () => {
    await page.goto(`${ui}/config-backups?deviceId=${encodeURIComponent(process.env.R72_AC1_ID || 'r68-ac1')}`, { waitUntil:'networkidle' })
    const bulk=page.getByRole('region',{name:'完整配置批量与定时备份'})
    await bulk.getByRole('button',{name:'预览完整备份目标',exact:true}).waitFor()
    const checkbox=page.locator('.check-row input[type=checkbox]').first()
    await checkbox.check()
    const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/complete-configuration/targets' && r.request().method()==='POST')
    await bulk.getByRole('button',{name:'预览完整备份目标',exact:true}).click()
    const resolved=await response
    const preview=await resolved.json()
    assert(resolved.ok() && preview.profileIds.length===1 && preview.items.length===1, 'Actual selected-device target preview failed')
    await bulk.locator('[aria-label="完整备份目标预览"]').waitFor()
    assert(await bulk.getByRole('button',{name:'执行完整批量备份',exact:true}).isEnabled(),'Resolved preview should allow explicit submission')
    const bulkReadiness = page.getByRole('region', { name: '多设备完整配置恢复前检查', exact: true })
    await bulkReadiness.getByRole('button', { name: '加载所选设备完整原件', exact: true }).click()
    await bulkReadiness.getByRole('combobox').waitFor()
    assert(await bulkReadiness.getByRole('button', { name: '执行多设备恢复前检查', exact: true }).isEnabled(), 'Stored source should enable explicit device preflight')
    await screenshots('complete-backup-target-preview')
    const panel=page.getByRole('region',{name:'模拟器完整配置'})
    const readiness = panel.getByRole('region',{name:'完整配置恢复前检查',exact:true})
    await readiness.waitFor()
    await readiness.getByRole('button', { name: '刷新检查历史', exact: true }).click()
    await readiness.getByRole('button', { name: '刷新检查历史', exact: true }).waitFor({ state: 'visible' })
    const refreshed=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/simulator-config/backups' && r.request().method()==='GET')
    await panel.getByRole('button',{name:'刷新完整备份',exact:true}).click()
    // The stored-history API is local; never click capture or restore-preview (which reads a device).
    await refreshed
    await page.waitForFunction(()=>!document.querySelector('[aria-label="完整配置基线"]')?.disabled)
    const baseline=panel.getByLabel('完整配置基线')
    const ids=await baseline.locator('option').evaluateAll(options=>options.filter(o=>o.value&&!o.disabled).map(o=>o.value))
    assert(ids.length>0,'No stored complete backup available for local preview')
    await baseline.selectOption(ids[0])
    await panel.getByRole('button',{name:'查看脱敏完整配置',exact:true}).click()
    await panel.locator('pre').first().waitFor()
    const config=await panel.locator('pre').first().innerText()
    assert(config.length>100,'Stored redacted configuration is empty')
    const configurationPending=page.waitForEvent('download')
    await panel.getByRole('button',{name:'下载脱敏完整配置',exact:true}).click()
    const configurationFile=await configurationPending
    const configurationPath=path.join(output,'complete-configuration-redacted.cfg')
    await configurationFile.saveAs(configurationPath)
    assert(readFileSync(configurationPath,'utf8')===config,'Downloaded redacted configuration must equal displayed stored view')
    await panel.getByLabel('比较源备份').selectOption(ids[0])
    await panel.getByLabel('比较目标备份').selectOption(ids[1]||ids[0])
    await panel.getByRole('button',{name:'比较完整配置',exact:true}).click()
    await panel.locator('[aria-label="配置比较结果"]').waitFor()
    const comparisonPending=page.waitForEvent('download')
    await panel.getByRole('button',{name:'导出脱敏比较报告',exact:true}).click()
    const comparisonFile=await comparisonPending
    const comparisonPath=path.join(output,'complete-configuration-diff.json')
    await comparisonFile.saveAs(comparisonPath)
    const comparisonJson=JSON.parse(readFileSync(comparisonPath,'utf8'))
    assert(comparisonJson.before.id===ids[0] && comparisonJson.after.id===(ids[1]||ids[0]),'Comparison download must retain selected source snapshot IDs')
    assert(Array.isArray(comparisonJson.contexts),'Comparison report must retain actual contexts')
    const schedules=(await read('/api/system/config-backup-schedules')).filter(item=>item.configurationKind==='COMPLETE_RUNNING')
    let recentExecution=null
    if(schedules.length){
      const rows=bulk.getByRole('table').filter({ has: page.getByRole('columnheader', { name: '固定设备范围', exact: true }) }).locator('tbody > tr')
      await rows.first().locator('summary').click()
      const detail=await rows.first().innerText()
      assert(schedules[0].profileIds.every(id=>detail.includes(id)),'Plan details must display frozen profile IDs')
      for(let i=0;i<schedules.length;i++){
        if(schedules[i].lastBatchId && schedules[i].lastBatchAvailable===false){
          assert(await rows.nth(i).getByRole('button',{name:'查看最近执行',exact:true}).isDisabled(),'Unavailable old batch must not offer a broken request')
          assert((await rows.nth(i).innerText()).includes('最近执行明细未保留'),'Unavailable old batch must retain an explicit explanation')
        }
      }
      const index=schedules.findIndex(plan=>plan.lastBatchId && plan.lastBatchAvailable!==false)
      if(index>=0){
        const response=page.waitForResponse(r=>new URL(r.url()).pathname===`/api/system/config-backups/batches/${schedules[index].lastBatchId}`)
        await rows.nth(index).getByRole('button',{name:'查看最近执行',exact:true}).click()
        recentExecution=await (await response).json()
        await bulk.locator('[aria-label="完整配置批量进度"]').waitFor()
        assert(recentExecution.id===schedules[index].lastBatchId,'Plan must open its actual most recent batch')
      }
    }
    await screenshots('complete-configuration-history')
    return {preview,storedBackups:ids.length,comparison:await panel.locator('[aria-label="配置比较结果"]').innerText(),downloads:{configuration:{filename:configurationFile.suggestedFilename(),path:configurationPath,bytes:Buffer.byteLength(config)},comparison:{filename:comparisonFile.suggestedFilename(),path:comparisonPath}},schedules,recentExecution,planLimitation:!schedules.length?'No stored complete configuration plans':!recentExecution?'No stored plan has a most recent execution':null,captured:false,scheduled:false,restored:false}
  })
  await step('quickPingStoredTrend', async () => {
    await navigate('工具集')
    const panel = page.locator('.quick-ping-history')
    await panel.getByRole('heading', { name: 'Quick Ping 历史趋势', exact: true }).waitFor()
    const load = panel.getByRole('button', { name: '加载 Quick Ping 趋势', exact: true })
    if (await load.isEnabled()) {
      await load.click()
      await panel.getByLabel('趋势 Site', { exact: true }).waitFor()
      assert(await panel.getByRole('alert').count() === 0, 'Stored trend history failed to load')
      assert(await panel.getByLabel('趋势设备', { exact: true }).count() === 1, 'Trend device filter missing')
      assert(await panel.getByLabel('趋势执行账号', { exact: true }).count() === 1, 'Trend actor filter missing')
      const exportButton = panel.getByRole('button', { name: '导出趋势 CSV', exact: true })
      if (await exportButton.isEnabled()) {
        const pending = page.waitForEvent('download')
        await exportButton.click()
        const download = await pending
        const target = path.join(output, 'quick-ping-trend.csv')
        await download.saveAs(target)
        const rows = parseCsv(readFileSync(target, 'utf8'))
        assert(rows.length > 0 && rows.every(row => row.device_id && row.site_id && row.actor && row.run_id && row.export_scope === 'LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS'), 'Trend CSV must preserve exact scoped history provenance')
      }
    } else {
      await panel.getByText('当前已加载历史没有 Quick Ping 任务。', { exact: true }).waitFor()
    }
    await screenshots('quick-ping-stored-trend')
    return { deviceProbes: 0, historyOnly: true }
  })
  await step('deviceLldpDetails', async () => {
    await navigate('设备管理')
    const row = page.locator('[data-testid="device-inventory-table"] tbody > tr').filter({ hasText: '192.168.56.3' }).first()
    await row.getByRole('button', { name: '详情', exact: true }).click()
    const panel = page.getByTestId('device-lldp-neighbors')
    await panel.waitFor()
    await panel.getByRole('status').waitFor({ state: 'hidden' })
    assert(await panel.getByRole('alert').count() === 0, 'Device LLDP claims failed')
    const table = panel.getByRole('table', { name: '设备 LLDP 邻居表', exact: true })
    await table.waitFor()
    assert(await table.locator('tbody > tr').count() === 3, 'Stored SW claims must retain three exact neighbor relations')
    await screenshots('device-lldp-details')
    await panel.getByRole('button', { name: '查看拓扑工作台', exact: true }).click()
    await page.getByRole('heading', { name: '拓扑与资产', exact: true }).waitFor()
    return { expectedStoredNeighbors: 3, deviceProbes: 0 }
  })
  await step('topology', async () => {
    await navigate('拓扑与资产')
    await page.getByLabel('LLDP 来源设备',{exact:true}).waitFor()
    const profileSelect=page.getByLabel('LLDP 来源设备',{exact:true})
    await page.waitForFunction(()=>!document.querySelector('[aria-label="LLDP 来源设备"]')?.disabled)
    const profiles=await read('/api/system/h3c/profiles')
    const claims=[]
    for(const profile of profiles) claims.push(await read(`/api/system/h3c/profiles/${encodeURIComponent(profile.id)}/lldp-claims?history=false&limit=1000`))
    const relations=claims.flatMap(claim=>claim.relations)
    const table=page.getByRole('table',{name:'LLDP 关系表',exact:true})
    const rows=table.locator('tbody > tr')
    const exportButton=page.getByRole('button',{name:'导出当前筛选 LLDP CSV',exact:true})
    const filters=[]
    let download=null
    if(relations.length){
      assert(await rows.count()===relations.length,'Loaded topology table must reflect actual local claims count')
      const selected=relations[0]
      await profileSelect.selectOption(selected.profileId)
      await page.getByLabel('LLDP 本地接口',{exact:true}).selectOption(selected.localInterface)
      const siteOptions=page.getByLabel('LLDP Site',{exact:true}).locator('option')
      const siteIndex=await siteOptions.evaluateAll((options,site)=>options.findIndex((option,index)=>index>0&&option.textContent===(site||'Site 未知')),selected.siteId)
      assert(siteIndex>0,'Actual relation Site must be offered as an exact filter')
      await page.getByLabel('LLDP Site',{exact:true}).selectOption({index:siteIndex})
      const expected=relations.filter(row=>row.profileId===selected.profileId&&row.localInterface===selected.localInterface&&row.siteId===selected.siteId)
      await page.waitForFunction(count=>document.querySelectorAll('[aria-label="LLDP 关系表"] tbody > tr').length===count,expected.length)
      assert(await rows.count()===expected.length,'Exact profile/interface/Site filters returned unexpected count')
      filters.push({profileId:selected.profileId,localInterface:selected.localInterface,siteId:selected.siteId,count:expected.length})
      const pending=page.waitForEvent('download')
      await exportButton.click()
      const file=await pending; const target=path.join(output,'lldp-current-filter.csv'); await file.saveAs(target)
      const csv=readFileSync(target,'utf8')
      assert(['profile_id','local_interface','site_id','source','fact_revision','claims_revision','CURRENT_FILTER_LOADED_CLAIMS_ONLY'].every(value=>csv.includes(value)),'Topology export lacks identity or source metadata')
      const exported=parseCsv(csv).filter(row=>row.profile_id)
      assert(exported.length===expected.length&&expected.every(row=>exported.some(cell=>
        cell.fact_revision===row.factRevision&&cell.source===row.source&&cell.relation_key===row.relationKey&&
        cell.source_authority===row.sourceAuthority&&cell.captured_scope_key===row.capturedScopeKey)),
        'Topology CSV must preserve filtered factual evidence and exact quoted source/scope keys')
      download={filename:file.suggestedFilename(),path:target,bytes:Buffer.byteLength(csv),rows:expected.length}
    }else{
      await table.getByText('暂无 LLDP 关系',{exact:true}).waitFor()
      assert(await exportButton.isDisabled(),'Empty LLDP facts must disable CSV export')
      assert(await profileSelect.locator('option').count()===1,'Empty claims must not invent device filter choices')
      assert(await page.getByLabel('LLDP 本地接口',{exact:true}).locator('option').count()===1,'Empty claims must not invent interfaces')
      assert(await page.getByLabel('LLDP Site',{exact:true}).locator('option').count()===1,'Empty claims must not invent Site claims')
    }
    await screenshots('topology-current-claims')
    return {profiles:profiles.length,claims,loadedRelations:relations.length,filters,download,exportVerified:!!download,limitation:!relations.length?'No stored LLDP relation; exact nonempty filters and CSV unavailable':null,deviceConnections:0}
  })
  await step('syslog', async () => {
    await navigate('工具集')
    const panel=page.locator('.syslog-workspace')
    const receiver=panel.locator('[aria-label="Syslog 接收状态"]')
    const receiverResponse=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/health')
    await receiver.getByRole('button',{name:'刷新接收状态',exact:true}).click()
    const receiverHealth=await (await receiverResponse).json()
    await receiver.getByText(new RegExp(`已接收 ${receiverHealth.udpReceived} 条`)).waitFor()
    const query=async()=>{
      const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/system/syslog/query'&&r.request().method()==='POST')
      await panel.getByRole('button',{name:'查询日志',exact:true}).click()
      const r=await response; const data=await r.json();assert(r.ok(),'Actual Syslog query failed'); return data
    }
    await panel.getByLabel('Syslog 时间范围').selectOption('168')
    const baseline=await query()
    const classificationChecks=[]
    if(process.env.R72_EXPECT_CLASSIFIED_SYSLOG==='1'){
      for(const type of ['MESH_LINKUP','IFNET_PHY_UPDOWN','UNKNOWN']){
        await panel.getByLabel('Syslog 事件类型').selectOption(type)
        await panel.getByLabel('Syslog 消息内容').fill('R72_CLASSIFICATION_FIXTURE')
        const filtered=await query()
        assert(filtered.items.length>0,`Expected local classification fixture ${type}`)
        assert(filtered.items.every(item=>item.eventType===type && item.rawReference && item.line>0),`Unexpected ${type} classification or missing Raw evidence`)
        classificationChecks.push({type,result:filtered})
      }
      await screenshots('syslog-event-classification')
      await panel.getByLabel('Syslog 事件类型').selectOption('')
      await panel.getByLabel('Syslog 消息内容').fill('')
      await query()
    }
    if(process.env.R72_EXPECT_LOCAL_SYSLOG==='1'){
      assert(receiverHealth.syslogEnabled && receiverHealth.udpReceived>=1 && receiverHealth.rawFlushedRecords>=1 && receiverHealth.udpDropped===0,'Expected enabled local receiver with received/flushed evidence and no drops')
      assert(baseline.items.some(item=>item.senderIp==='127.0.0.1' && item.facility===16 && item.severity===6 && item.message.includes('R72_LOCAL_TEST')),'Expected parent-sent local UDP fixture must retain sender and PRI evidence')
    }
    let download=null
    if(baseline.items.length){
      const pending=page.waitForEvent('download')
      await panel.getByRole('button',{name:'导出当前结果',exact:true}).click()
      const file=await pending;const target=path.join(output,'syslog-query.csv');await file.saveAs(target)
      const csv=readFileSync(target,'utf8')
      assert(csv.includes('接收时间') && csv.includes('来源 IP') && csv.includes('Raw'),'CSV header lacks evidence columns')
      assert(csv.includes('事件族') && csv.includes('事件类型'),'CSV must retain event classification columns')
      assert(csv.split('\r\n').length===baseline.items.length+1,'CSV row count must equal current query result')
      assert(baseline.items.every(item=>csv.includes(item.rawReference)),'CSV must retain query Raw references')
      download={filename:file.suggestedFilename(),path:target,bytes:Buffer.byteLength(csv),rows:baseline.items.length}
    } else assert(await panel.getByRole('button',{name:'导出当前结果',exact:true}).isDisabled(),'Empty result must disable export')
    await screenshots('syslog-query')
    await panel.getByLabel('Syslog 来源 IP').fill('192.0.2.254')
    await panel.getByLabel('Syslog 最高级别').selectOption('2')
    await panel.getByLabel('Syslog 消息内容').fill('R72_BROWSER_ABSENT_TOKEN_20261108')
    const empty=await query()
    assert(empty.items.length===0,'Exact Syslog filters must produce empty result')
    await panel.getByText('当前条件没有已封存日志。请检查接收设置、来源和时间范围。',{exact:true}).waitFor()
    assert(await panel.getByRole('button',{name:'导出当前结果',exact:true}).isDisabled(),'Empty filtered result must disable export')
    await screenshots('syslog-filter-empty')
    return {receiverHealth,baseline,classificationChecks,empty,download,udpSent:false,exportVerified:!!download}
  })
  result.overall=Object.values(result.checks).some(c=>c.status==='FAIL')||result.pageErrors.length||result.apiFailures.length||result.consoleErrors.length?'PARTIAL':'PASS_WITH_LIMITATIONS'
} catch(error){result.overall='BLOCKED';result.error=String(error)}
finally{result.completedAt=new Date().toISOString();save();await page.unrouteAll({behavior:'ignoreErrors'});await browser.close()}
console.log(JSON.stringify({output,overall:result.overall,checks:Object.fromEntries(Object.entries(result.checks).map(([key,value])=>[key,value.status])),apiFailures:result.apiFailures,pageErrors:result.pageErrors,consoleErrors:result.consoleErrors},null,2))
if(result.overall!=='PASS_WITH_LIMITATIONS')process.exitCode=1
