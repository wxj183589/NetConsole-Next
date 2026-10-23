<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { SearchAddon } from '@xterm/addon-search'
import { ElMessage, ElMessageBox } from 'element-plus'
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

const terminalTheme = { background: '#0b111a', foreground: '#dbe8f8', cursor: '#5d9cff', selectionBackground: '#294a76' }
function activeTab(): TerminalTab | undefined { return tabs.find((tab) => tab.id === activeId.value) }
function visibleTabs(): TerminalTab[] { const active = activeTab(); if (!active) return []; return split.value ? tabs.filter((tab) => tab.id === active.id || tab.id === tabs.find((candidate) => candidate.id !== active.id)?.id) : [active] }
function setPaneRef(id: string, element: Element | null): void {
  if (element instanceof HTMLElement) { paneRefs.set(id, element); mountTerminal(tabs.find((tab) => tab.id === id)) }
}
function mountTerminal(tab?: TerminalTab): void {
  if (!tab || tab.terminal || !paneRefs.has(tab.id)) return
  const terminal = new Terminal({ convertEol: true, cursorBlink: true, scrollback: 5000, fontSize: 13, theme: terminalTheme })
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
async function openDevice(device: TerminalDevice, protocol: string): Promise<void> {
  const tab: TerminalTab = { id: crypto.randomUUID(), label: `${device.host} · ${protocol}`, targetId: device.id, protocol, inputBuffer: '', connected: false }
  tabs.push(tab); activeId.value = tab.id; await nextTick(); mountTerminal(tab)
  try {
    const session = await createTerminalSession({ targetId: device.id, protocol, mode: 'RESTRICTED_READONLY' }); tab.sessionId = session.sessionId
    const ticket = await issueTerminalTicket(tab.sessionId); const scheme = location.protocol === 'https:' ? 'wss' : 'ws'
    tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket.ticket)}`)
    tab.socket.binaryType = 'arraybuffer'
    tab.socket.onopen = () => { tab.connected = true; tab.fit?.fit(); tab.terminal?.writeln('\x1b[32m已连接\x1b[0m') }
    tab.socket.onmessage = async (event) => {
      if (typeof event.data === 'string') { try { const message = JSON.parse(event.data); if (message.type === 'error') tab.terminal?.writeln(`\r\n\x1b[31m${message.error}\x1b[0m`) } catch { /* ignore malformed status */ } }
      else tab.terminal?.write(new Uint8Array(event.data instanceof ArrayBuffer ? event.data : await event.data.arrayBuffer()))
    }
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
async function openSerial(): Promise<void> {
  try { serialPorts.value = await getSerialPorts(); if (!serialPorts.value.length) { ElMessage.info('服务端未发现可用 COM 端口'); return }; selectedSerialPort.value = serialPorts.value[0]; serialDialog.value = true } catch { ElMessage.error('COM 端口读取失败') }
}
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
async function refreshAll(): Promise<void> { loading.value = true; try { devices.value = await getTerminalDevices(); await refreshHistory() } catch { ElMessage.error('终端能力读取失败') } finally { loading.value = false } }
onMounted(() => refreshAll())
onBeforeUnmount(() => { stopReplay(); tabs.forEach((tab) => { tab.socket?.close(); tab.terminal?.dispose() }) })
</script>

<template>
  <NcLayout title="终端控制台" description="SSH / Telnet / COM · 受控只读会话 · 录制、回放与导出">
    <template #actions><el-button size="small" @click="refreshAll">刷新</el-button><el-button size="small" @click="openSerial">打开 COM</el-button></template>
    <div class="terminal-layout">
      <aside class="card terminal-sidebar"><div class="card__header"><h2>登记设备</h2><span class="state-pill">{{ devices.length }}</span></div><div class="device-list"><div v-for="device in devices" :key="device.id" class="device-list__item"><strong>{{ device.host }}:{{ device.port }}</strong><small>{{ device.username }} · {{ device.enabled ? '已授权' : '已禁用' }}</small><div class="toolbar"><el-button v-for="protocol in device.supportedProtocols" :key="protocol" text size="small" :disabled="!device.enabled" @click="openDevice(device, protocol)">{{ protocol }}</el-button></div></div><div v-if="!devices.length" class="empty-state"><div><strong>暂无可用设备</strong><small>设备必须先由服务端登记；不生成模拟设备。</small></div></div></div><div class="card__header"><h2>会话历史</h2><el-button text size="small" @click="refreshHistory">刷新</el-button></div><div class="history-list"><div v-for="session in sessions" :key="session.id" class="history-item" @click="openHistory(session)"><strong>{{ session.targetName }}</strong><small>{{ session.protocol }} · <span :class="['state-pill', stateClass(session.recordingStatus)]">{{ session.recordingStatus }}</span></small><small>{{ formatTime(session.startedMs) }}</small></div><div v-if="!sessions.length" class="empty-state"><div><strong>暂无会话</strong><small>终端会话关闭后会保留在服务端 Catalog 中。</small></div></div></div></aside>
      <section class="card terminal-main"><div class="terminal-toolbar"><el-button size="small" :disabled="!tabs.length" @click="split = !split">{{ split ? '取消分屏' : '分屏' }}</el-button><el-button size="small" :disabled="!activeTab()" @click="reconnect">重连</el-button><el-button size="small" :disabled="!activeTab()" @click="activeTab() && closeTab(activeTab()!)">断开</el-button><div class="terminal-toolbar__grow" /><input v-model="search" placeholder="搜索当前终端，回车定位" @keydown.enter="activeTab()?.search?.findNext(search)" /></div><div class="terminal-tabs"><button v-for="tab in tabs" :key="tab.id" :class="['terminal-tab', { active: activeId === tab.id }]" type="button" @click="activeId = tab.id"><span>{{ tab.label }} {{ tab.connected ? '●' : '○' }}</span><span class="terminal-tab__close" @click.stop="closeTab(tab)">×</span></button><span v-if="!tabs.length" style="padding:8px 10px;color:#758aa8;font-size:11px">从左侧选择设备打开受控终端</span></div><div :class="['terminal-panes', { split }]" v-if="tabs.length"><div v-for="tab in visibleTabs()" :key="tab.id" class="terminal-pane"><div :ref="(element) => setPaneRef(tab.id, element as Element | null)" class="terminal-host" /></div></div><div v-else class="terminal-placeholder">内置终端尚未打开会话</div></section>
    </div>
    <el-dialog v-model="serialDialog" title="打开本地 COM Console" width="420px"><label class="form-field"><span>端口</span><select v-model="selectedSerialPort"><option v-for="port in serialPorts" :key="port" :value="port">{{ port }}</option></select></label><template #footer><el-button @click="serialDialog = false">取消</el-button><el-button type="primary" @click="openSerialSession">打开</el-button></template></el-dialog>
    <el-dialog :model-value="historyDialog" title="会话历史 / 只读回放" width="860px" @close="closeHistory"><template v-if="historySession"><div class="toolbar" style="margin-bottom:12px"><span>{{ historySession.targetName }} · {{ historySession.protocol }}</span><span :class="['state-pill', stateClass(historyIntegrity?.status || '')]">{{ historyIntegrity?.status || '读取中' }}</span><span class="state-pill">{{ historyIntegrity?.hashMatches ? 'SHA-256 匹配' : '完整性待确认' }}</span><div style="flex:1" /><el-button size="small" @click="exportSession('TXT')">TXT</el-button><el-button size="small" @click="exportSession('JSONL')">JSONL</el-button><el-button size="small" @click="exportSession('HTML')">HTML</el-button></div><div class="toolbar" style="margin-bottom:12px"><el-button size="small" @click="replayIndex = 0; stopReplay()">重新开始</el-button><el-button size="small" @click="playReplay">播放</el-button><label style="font-size:12px;color:var(--nc-text-secondary)">速度 <select v-model.number="replaySpeed"><option :value="0.5">0.5x</option><option :value="1">1x</option><option :value="2">2x</option><option :value="4">4x</option></select></label><input v-model="replaySearch" placeholder="搜索回放" style="min-height:30px;border:1px solid var(--nc-border);border-radius:6px;padding:0 8px" /></div><pre class="history-replay">{{ replaySearch ? replayText().replace(new RegExp(replaySearch.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi'), (match) => `【${match}】`) : replayText() }}</pre><div class="section-label">事件时间轴</div><div class="table-wrap"><table class="data-table"><tbody><tr v-for="event in historyEvents" :key="`${event.atMs}-${event.eventType}`"><td>{{ formatTime(event.atMs) }}</td><td>{{ event.eventType }}</td><td class="muted mono">{{ event.detailJson }}</td></tr></tbody></table></div></template></el-dialog>
  </NcLayout>
</template>

