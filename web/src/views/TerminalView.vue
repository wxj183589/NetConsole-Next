<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Connection, Document, Monitor, Search, Setting, SwitchButton } from '@element-plus/icons-vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { SearchAddon } from '@xterm/addon-search'
import NcLayout from '../components/NcLayout.vue'
import {
  closeTerminalSession, createTerminalSession, downloadExport, getRecordingIntegrity, getReplay, getSerialPorts,
  getTerminalDevices, getTerminalEvents, getTerminalSessions, issueTerminalTicket,
  type TerminalDevice, type TerminalSession,
} from '../api/client'

interface TerminalTab {
  id: string; label: string; targetId: string; protocol: string; sessionId?: string; socket?: WebSocket
  terminal?: Terminal; fit?: FitAddon; search?: SearchAddon; inputBuffer: string; connected: boolean
}
interface ReplayEntry { atMs: number; direction: string; dataB64: string }

const devices = ref<TerminalDevice[]>([])
const deviceSearch = ref('')
const activeDeviceId = ref('')
const selectedProtocol = ref('AUTO')
const route = useRoute()
const sessions = ref<TerminalSession[]>([])
const tabs = reactive<TerminalTab[]>([])
const activeId = ref('')
const split = ref(false)
const search = ref('')
const loading = ref(false)
const paneRefs = new Map<string, HTMLElement>()
const serialDialog = ref(false)
const serialPorts = ref<string[]>([])
const selectedSerialPort = ref('')
const historyDialog = ref(false)
const historySession = ref<TerminalSession | null>(null)
const historyEvents = ref<Array<{ atMs: number; eventType: string; detailJson: string }>>([])
const historyIntegrity = ref<{ status: string; detail: string; hashMatches: boolean } | null>(null)
const replayEntries = ref<ReplayEntry[]>([])
const replayIndex = ref(0)
const replaySpeed = ref(1)
const replaySearch = ref('')
let replayTimer: ReturnType<typeof setTimeout> | undefined

const terminalTheme = { background: '#08111d', foreground: '#dbe8f8', cursor: '#5d9cff', selectionBackground: '#294a76' }
const filteredDevices = computed(() => {
  const query = deviceSearch.value.trim().toLowerCase()
  return devices.value.filter(device => !query || `${device.id} ${device.host} ${device.username}`.toLowerCase().includes(query))
})
const selectedDevice = computed(() => devices.value.find(device => device.id === activeDeviceId.value) || null)
function activeTab(): TerminalTab | undefined { return tabs.find((tab) => tab.id === activeId.value) }
function visibleTabs(): TerminalTab[] { const active = activeTab(); if (!active) return []; return split.value ? tabs.filter((tab) => tab.id === active.id || tab.id === tabs.find((candidate) => candidate.id !== active.id)?.id) : [active] }
function setPaneRef(id: string, element: Element | null): void { if (element instanceof HTMLElement) { paneRefs.set(id, element); mountTerminal(tabs.find((tab) => tab.id === id)) } }
function mountTerminal(tab?: TerminalTab): void {
  if (!tab || tab.terminal || !paneRefs.has(tab.id)) return
  const terminal = new Terminal({ convertEol: true, cursorBlink: true, scrollback: 5000, fontSize: 13, fontFamily: 'Cascadia Mono, Consolas, monospace', theme: terminalTheme })
  const fit = new FitAddon(); const searchAddon = new SearchAddon()
  terminal.loadAddon(fit); terminal.loadAddon(searchAddon); tab.terminal = terminal; tab.fit = fit; tab.search = searchAddon
  terminal.open(paneRefs.get(tab.id)!); fit.fit(); terminal.onResize((size) => sendSocket(tab, { type: 'resize', columns: size.cols, rows: size.rows }))
  terminal.onData((data) => handleInput(tab, data)); terminal.writeln(`\x1b[90m${tab.label} · RESTRICTED_READONLY\x1b[0m`)
}
function handleInput(tab: TerminalTab, data: string): void {
  if (!tab.connected) return
  if (data === '\u0003') { tab.inputBuffer = ''; tab.terminal?.writeln('^C'); sendSocket(tab, { type: 'control', signal: 'CTRL_C' }); return }
  if (data === '\r' || data === '\n') { const command = tab.inputBuffer; tab.inputBuffer = ''; tab.terminal?.write('\r\n'); if (command) sendSocket(tab, { type: 'input', text: command }); return }
  if (data === '\u007f' || data === '\b') { if (tab.inputBuffer.length) { tab.inputBuffer = tab.inputBuffer.slice(0, -1); tab.terminal?.write('\b \b') }; return }
  if (data.includes('\r') || data.includes('\n')) { tab.inputBuffer += data.replace(/[\r\n]/g, ''); tab.terminal?.write(data.replace(/[\r\n]/g, '')); return }
  if ([...data].some((character) => character.charCodeAt(0) < 0x20)) return
  tab.inputBuffer += data; tab.terminal?.write(data)
}
function sendSocket(tab: TerminalTab, message: Record<string, unknown>): void { if (tab.socket?.readyState === WebSocket.OPEN) tab.socket.send(JSON.stringify(message)) }
function selectDevice(device: TerminalDevice): void {
  activeDeviceId.value = device.id
  selectedProtocol.value = device.supportedProtocols.includes('SSH') ? 'SSH' : device.supportedProtocols[0] || 'AUTO'
}
async function openSelectedDevice(): Promise<void> { if (selectedDevice.value) await openDevice(selectedDevice.value, selectedProtocol.value) }
async function openDevice(device: TerminalDevice, protocol: string): Promise<void> {
  selectDevice(device); selectedProtocol.value = protocol
  const tab: TerminalTab = { id: crypto.randomUUID(), label: `${device.host} · ${protocol}`, targetId: device.id, protocol, inputBuffer: '', connected: false }
  tabs.push(tab); activeId.value = tab.id; await nextTick(); mountTerminal(tab)
  try {
    const session = await createTerminalSession({ targetId: device.id, protocol, mode: 'RESTRICTED_READONLY' }); tab.sessionId = session.sessionId
    const ticket = await issueTerminalTicket(tab.sessionId); const scheme = location.protocol === 'https:' ? 'wss' : 'ws'
    tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket.ticket)}`)
    tab.socket.binaryType = 'arraybuffer'
    tab.socket.onopen = () => { tab.connected = true; tab.fit?.fit(); tab.terminal?.writeln('\x1b[32m已连接\x1b[0m') }
    tab.socket.onmessage = async (event) => { if (typeof event.data === 'string') { try { const message = JSON.parse(event.data); if (message.type === 'error') tab.terminal?.writeln(`\r\n\x1b[31m${message.error}\x1b[0m`) } catch { /* ignore malformed status */ } } else tab.terminal?.write(new Uint8Array(event.data instanceof ArrayBuffer ? event.data : await event.data.arrayBuffer())) }
    tab.socket.onclose = () => { tab.connected = false; tab.terminal?.writeln('\r\n\x1b[90m已断开\x1b[0m') }
    tab.socket.onerror = () => ElMessage.error(`${tab.label} WebSocket 连接失败`)
  } catch (cause) { tab.terminal?.writeln(`\r\n\x1b[31m${cause instanceof Error ? cause.message : '连接失败'}\x1b[0m`); ElMessage.error('终端会话创建失败') }
}
async function closeTab(tab: TerminalTab): Promise<void> {
  if (tab.sessionId) { try { await closeTerminalSession(tab.sessionId) } catch { /* session may already be closed */ } }
  tab.socket?.close(); tab.terminal?.dispose(); paneRefs.delete(tab.id); const index = tabs.indexOf(tab); if (index >= 0) tabs.splice(index, 1); if (activeId.value === tab.id) activeId.value = tabs[Math.max(0, index - 1)]?.id || tabs[0]?.id || ''
  await refreshHistory()
}
async function reconnect(): Promise<void> { const tab = activeTab(); if (!tab?.sessionId) return; tab.socket?.close(); try { const ticket = await issueTerminalTicket(tab.sessionId); const scheme = location.protocol === 'https:' ? 'wss' : 'ws'; tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket.ticket)}`) } catch { ElMessage.error('重连失败') } }
async function openSerial(): Promise<void> { try { serialPorts.value = await getSerialPorts(); if (!serialPorts.value.length) { ElMessage.info('服务端未发现可用 COM 端口'); return }; selectedSerialPort.value = serialPorts.value[0]; serialDialog.value = true } catch { ElMessage.error('COM 端口读取失败') } }
async function openSerialSession(): Promise<void> {
  serialDialog.value = false
  const tab: TerminalTab = { id: crypto.randomUUID(), label: selectedSerialPort.value, targetId: `serial:${selectedSerialPort.value}`, protocol: 'SERIAL', inputBuffer: '', connected: false }
  tabs.push(tab); activeId.value = tab.id; await nextTick(); mountTerminal(tab)
  try { const session = await createTerminalSession({ targetId: tab.targetId, protocol: 'SERIAL', mode: 'RESTRICTED_READONLY', serialPort: selectedSerialPort.value }); tab.sessionId = session.sessionId; const ticket = await issueTerminalTicket(tab.sessionId); const scheme = location.protocol === 'https:' ? 'wss' : 'ws'; tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket.ticket)}`); tab.socket.binaryType = 'arraybuffer'; tab.socket.onopen = () => { tab.connected = true; tab.fit?.fit() }; tab.socket.onmessage = (event) => { if (event.data instanceof ArrayBuffer) tab.terminal?.write(new Uint8Array(event.data)) } } catch { ElMessage.error('COM 会话创建失败') }
}
async function refreshHistory(): Promise<void> { try { sessions.value = (await getTerminalSessions()).items } catch { sessions.value = [] } }
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string { return ['CONNECTED', 'COMPLETE', 'COMPLETED'].includes(state) ? 'good' : ['FAILED', 'CORRUPTED'].includes(state) ? 'bad' : 'warn' }
function decode(value: string): string { try { const bytes = Uint8Array.from(atob(value), (character) => character.charCodeAt(0)); return new TextDecoder().decode(bytes) } catch { return '[无法解码]' } }
async function openHistory(session: TerminalSession): Promise<void> {
  historySession.value = session; historyDialog.value = true; replayIndex.value = 0; replayEntries.value = []; replaySearch.value = ''
  try { [historyEvents.value, historyIntegrity.value] = await Promise.all([getTerminalEvents(session.id), getRecordingIntegrity(session.id)]); let offset = 0; do { const page = await getReplay(session.id, offset); replayEntries.value.push(...page.entries); offset += page.entries.length; if (!page.hasMore || !page.entries.length) break } while (replayEntries.value.length < 10000) } catch { ElMessage.error('会话回放读取失败') }
}
function replayText(): string { return replayEntries.value.slice(0, replayIndex.value).filter((entry) => entry.direction === 'output').map((entry) => decode(entry.dataB64)).join('') }
function stopReplay(): void { if (replayTimer) clearTimeout(replayTimer); replayTimer = undefined }
function playReplay(): void { stopReplay(); if (replayIndex.value >= replayEntries.value.length) replayIndex.value = 0; const step = () => { if (replayIndex.value >= replayEntries.value.length) return; const current = replayEntries.value[replayIndex.value]; replayIndex.value += 1; replayTimer = setTimeout(step, Math.max(12, ((replayEntries.value[replayIndex.value]?.atMs || current.atMs + 120) - current.atMs) / replaySpeed.value)) }; step() }
async function exportSession(format: string): Promise<void> { if (!historySession.value) return; try { const blob = await downloadExport(historySession.value.id, format); const link = document.createElement('a'); link.href = URL.createObjectURL(blob); link.download = `terminal-${historySession.value.id}.${format.toLowerCase()}`; link.click(); URL.revokeObjectURL(link.href) } catch { ElMessage.error('导出失败') } }
function closeHistory(): void { stopReplay(); historyDialog.value = false }
async function refreshAll(): Promise<void> { loading.value = true; try { devices.value = await getTerminalDevices(); if (!activeDeviceId.value && devices.value[0]) selectDevice(devices.value[0]); await refreshHistory() } catch { devices.value = []; sessions.value = []; ElMessage.info('当前服务未提供可用终端设备，页面保留真实空状态') } finally { loading.value = false } }
onMounted(async () => { await refreshAll(); const requested = String(route.query.deviceId || ''); const device = devices.value.find(item => item.id === requested); if (device) { selectDevice(device); await openDevice(device, selectedProtocol.value) } })
onBeforeUnmount(() => { stopReplay(); tabs.forEach((tab) => { tab.socket?.close(); tab.terminal?.dispose() }) })
</script>

<template>
  <NcLayout title="终端控制台" description="SSH / Telnet / COM · 受控只读会话 · 录制、回放与导出">
    <div class="terminal-workbench">
      <aside class="terminal-device-panel">
        <div class="terminal-panel-head"><div><h2>设备列表</h2><small>{{ devices.length }} 条当前可用记录</small></div><el-button text size="small" :loading="loading" @click="refreshAll">刷新</el-button></div>
        <div class="terminal-device-search"><label class="global-search"><el-icon><Search /></el-icon><input v-model="deviceSearch" placeholder="搜索设备、地址…" /></label></div>
        <div class="terminal-device-list"><button v-for="device in filteredDevices" :key="device.id" data-testid="terminal-device-row" :data-device-id="device.id" :class="['terminal-device-row', { 'is-active': activeDeviceId === device.id }]" type="button" @click="selectDevice(device)"><strong>{{ device.host || '未命名设备' }}</strong><small>{{ device.username || '—' }} · {{ device.enabled ? '已授权' : '已禁用' }}</small><span class="terminal-device-row__meta"><span>{{ device.terminalProtocol || 'AUTO' }}</span><span :class="['state-pill', device.enabled ? 'good' : 'bad']">{{ device.enabled ? '可用' : '禁用' }}</span></span></button><div v-if="!filteredDevices.length" class="terminal-history-empty"><div><el-icon><Monitor /></el-icon><p>暂无可用设备</p><small>服务端必须先登记真实设备。</small></div></div></div>
      </aside>

      <section class="terminal-inspector">
        <div class="terminal-panel-head"><div><h2>连接配置</h2><small>根据登记 Profile 展示</small></div><el-icon><Setting /></el-icon></div>
        <div v-if="selectedDevice" class="terminal-inspector__body">
          <div class="terminal-device-title"><el-icon><Connection /></el-icon><div><h2>{{ selectedDevice.id }}</h2><p>{{ selectedDevice.host }}:{{ selectedDevice.port }} · {{ selectedDevice.username }}</p></div></div>
          <dl class="terminal-detail-list"><div class="terminal-detail-row"><dt>协议</dt><dd>{{ selectedDevice.terminalProtocol || 'AUTO' }}</dd></div><div class="terminal-detail-row"><dt>Host Key</dt><dd>{{ selectedDevice.hostKeyUpdatePolicy }}</dd></div><div class="terminal-detail-row"><dt>编码</dt><dd>{{ selectedDevice.terminalEncoding }}</dd></div><div class="terminal-detail-row"><dt>凭据</dt><dd>{{ selectedDevice.username ? '由服务端引用' : '未配置' }}</dd></div></dl>
          <div class="protocol-switch" data-testid="protocol-switch"><button v-for="protocol in selectedDevice.supportedProtocols" :key="protocol" :class="{ 'is-active': selectedProtocol === protocol }" type="button" @click="selectedProtocol = protocol">{{ protocol === 'SERIAL' ? 'COM / Console' : protocol }}</button></div>
          <div class="terminal-params"><div class="terminal-param"><span>地址</span><span class="mono">{{ selectedDevice.host }}:{{ selectedDevice.port }}</span></div><div class="terminal-param"><span>模式</span><span>RESTRICTED_READONLY</span></div><div class="terminal-param"><span>回退</span><span>{{ selectedDevice.allowTelnetFallback ? '允许 Telnet' : '按 Profile 约束' }}</span></div></div>
          <el-button class="terminal-open-button" data-testid="terminal-open-button" type="primary" :disabled="!selectedDevice.enabled" @click="openSelectedDevice"><el-icon><Connection /></el-icon>打开受控终端</el-button>
        </div>
        <div v-else class="terminal-history-empty"><div><el-icon><Document /></el-icon><p>请选择设备</p><small>当前预览不创建模拟设备或连接。</small></div></div>
      </section>

      <section class="terminal-console">
        <div class="terminal-console__toolbar"><span v-if="activeTab()" class="terminal-status"><i class="status-dot status-dot--success" />{{ activeTab()?.connected ? '受限只读' : '未连接' }}</span><span v-else class="terminal-status"><i class="status-dot" />等待会话</span><span class="toolbar-spacer" /><el-button size="small" :disabled="!tabs.length" @click="split = !split">{{ split ? '取消分屏' : '分屏' }}</el-button><el-button size="small" :disabled="!activeTab()" @click="reconnect">重连</el-button><el-button size="small" :disabled="!activeTab()" @click="activeTab() && closeTab(activeTab()!)"><el-icon><SwitchButton /></el-icon>断开</el-button><el-button size="small" @click="openSerial">COM</el-button><input v-model="search" placeholder="搜索终端" @keydown.enter="activeTab()?.search?.findNext(search)" /></div>
        <div class="terminal-tabs"><button v-for="tab in tabs" :key="tab.id" :class="['terminal-tab', { active: activeId === tab.id }]" type="button" @click="activeId = tab.id"><span>{{ tab.label }} {{ tab.connected ? '●' : '○' }}</span><span class="terminal-tab__close" @click.stop="closeTab(tab)">×</span></button><span v-if="!tabs.length" style="padding:8px 10px;color:#758aa8;font-size:11px">从左侧选择设备打开受控终端</span></div>
        <div :class="['terminal-panes', { split }]" v-if="tabs.length"><div v-for="tab in visibleTabs()" :key="tab.id" class="terminal-pane"><div :ref="(element) => setPaneRef(tab.id, element as Element | null)" class="terminal-host" /></div></div><div v-else class="terminal-placeholder"><div><el-icon><Connection /></el-icon><p>内置终端尚未打开会话</p><small>SSH、Telnet、COM 都必须经过服务端受控会话。</small></div></div>
      </section>

<section class="terminal-history-dock"><div class="terminal-history-tabs"><button class="terminal-history-tab is-active" type="button"><el-icon><Document /></el-icon> 会话历史</button><button class="terminal-history-tab" type="button">终端回放</button><button class="terminal-history-tab" type="button">任务记录</button><span class="toolbar-spacer" /><el-button text size="small" @click="refreshHistory">刷新历史</el-button></div><div class="terminal-history-body"><table v-if="sessions.length" class="terminal-history-table"><thead><tr><th>目标设备</th><th>协议</th><th>开始时间</th><th>录制状态</th><th>完整性</th><th>操作</th></tr></thead><tbody><tr v-for="session in sessions.slice(0, 6)" :key="session.id" data-testid="terminal-history-row"><td>{{ session.targetName }}</td><td>{{ session.protocol }}</td><td>{{ formatTime(session.startedMs) }}</td><td><span :class="['state-pill', stateClass(session.recordingStatus)]">{{ session.recordingStatus }}</span></td><td>{{ session.recordingComplete ? 'COMPLETE' : '待确认' }}</td><td><el-button text size="small" @click="openHistory(session)">回放 / 导出</el-button></td></tr></tbody></table><div v-else class="terminal-history-empty"><div><el-icon><Document /></el-icon><p>暂无会话历史</p><small>终端会话结束后，服务端才会出现可回放记录。</small></div></div></div></section>
    </div>

    <el-dialog v-model="serialDialog" title="打开本地 COM Console" width="420px"><label class="form-field"><span>端口</span><select v-model="selectedSerialPort"><option v-for="port in serialPorts" :key="port" :value="port">{{ port }}</option></select></label><template #footer><el-button @click="serialDialog = false">取消</el-button><el-button type="primary" @click="openSerialSession">打开</el-button></template></el-dialog>
    <el-dialog :model-value="historyDialog" title="会话历史 / 只读回放" width="860px" @close="closeHistory"><template v-if="historySession"><div class="toolbar" style="margin-bottom:12px"><span>{{ historySession.targetName }} · {{ historySession.protocol }}</span><span :class="['state-pill', stateClass(historyIntegrity?.status || '')]">{{ historyIntegrity?.status || '读取中' }}</span><span class="state-pill">{{ historyIntegrity?.hashMatches ? 'SHA-256 匹配' : '完整性待确认' }}</span><div style="flex:1" /><el-button size="small" @click="exportSession('TXT')">TXT</el-button><el-button size="small" @click="exportSession('JSONL')">JSONL</el-button><el-button size="small" @click="exportSession('HTML')">HTML</el-button></div><div class="toolbar" style="margin-bottom:12px"><el-button size="small" @click="replayIndex = 0; stopReplay()">重新开始</el-button><el-button size="small" @click="playReplay">播放</el-button><label style="font-size:12px;color:var(--nc-text-secondary)">速度 <select v-model.number="replaySpeed"><option :value="0.5">0.5x</option><option :value="1">1x</option><option :value="2">2x</option><option :value="4">4x</option></select></label><input v-model="replaySearch" placeholder="搜索回放" style="min-height:30px;border:1px solid var(--nc-border);border-radius:6px;padding:0 8px" /></div><pre class="history-replay">{{ replaySearch ? replayText().replace(new RegExp(replaySearch.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi'), (match) => `【${match}】`) : replayText() }}</pre><div class="section-label">事件时间轴</div><div class="table-wrap"><table class="data-table"><tbody><tr v-for="event in historyEvents" :key="`${event.atMs}-${event.eventType}`"><td>{{ formatTime(event.atMs) }}</td><td>{{ event.eventType }}</td><td class="muted mono">{{ event.detailJson }}</td></tr></tbody></table></div></template></el-dialog>
  </NcLayout>
</template>
