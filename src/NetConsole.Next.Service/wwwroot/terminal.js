import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { SearchAddon } from "@xterm/addon-search";

(() => {
  "use strict";
  const state = { token: sessionStorage.getItem("netconsole-token") || "", devices: [], sessions: [], total: 0, historyPage: 1, tabs: new Map(), active: null, split: false, dark: true, history: null };
  const $ = id => document.getElementById(id);
  const safeText = value => String(value ?? "").replace(/[&<>\"']/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", "\"":"&quot;", "'":"&#39;" }[c]));
  const notice = (text, error = false) => { $("notice").textContent = text || ""; $("notice").classList.toggle("error", error); };
  const api = async (path, options = {}) => {
    const headers = new Headers(options.headers || {}); if (state.token) headers.set("Authorization", `Bearer ${state.token}`);
    const response = await fetch(path, { ...options, headers });
    if (!response.ok) { const body = await response.text(); throw new Error(`${response.status}: ${body || response.statusText}`); }
    return response.status === 204 ? null : response.json();
  };
  const formatTime = (ms, zone = $("history-timezone")?.value || "Asia/Shanghai") => {
    if (!ms) return "-";
    const timeZone = zone === "local" ? Intl.DateTimeFormat().resolvedOptions().timeZone : zone;
    return new Intl.DateTimeFormat("zh-CN", { timeZone, dateStyle:"medium", timeStyle:"medium" }).format(new Date(ms));
  };
  const eventDetail = value => { try { return JSON.stringify(JSON.parse(value || "{}")); } catch { return String(value || "{}"); } };

  function renderDevices() {
    const list = $("device-list"); list.replaceChildren();
    if (!state.devices.length) { list.innerHTML = '<div class="muted">没有可用的已登记设备</div>'; return; }
    for (const device of state.devices) {
      const item = document.createElement("div"); item.className = `device${device.enabled ? "" : " disabled"}`;
      item.innerHTML = `<strong>${safeText(device.id)}</strong><small>${safeText(device.host)}:${device.port} · ${safeText(device.username)}</small><small>${device.enabled ? "已授权" : "已禁用"}${device.lastSessionState ? ` · 最近 ${safeText(device.lastSessionState)}` : ""}</small>`;
      const actions = document.createElement("div"); actions.className = "device-actions";
      for (const protocol of ["SSH", "TELNET"]) { const button = document.createElement("button"); button.textContent = protocol; button.disabled = !device.enabled; button.onclick = () => createTab(device, protocol); actions.append(button); }
      item.append(actions); list.append(item);
    }
    const serial = document.createElement("button"); serial.textContent = "打开本地 COM Console"; serial.style.width = "100%"; serial.style.marginTop = "8px"; serial.onclick = openSerialDialog; list.append(serial);
    const historyDevice = $("history-device");
    if (historyDevice) {
      const selected = historyDevice.value;
      historyDevice.replaceChildren(new Option("全部设备", ""), ...state.devices.map(device => new Option(`${device.id} · ${device.host}`, device.id)));
      historyDevice.value = selected;
    }
  }

  function renderSessions() {
    const list = $("session-list"); list.replaceChildren();
    for (const session of state.sessions) {
      const item = document.createElement("div"); item.className = "session";
      item.innerHTML = `<strong>${safeText(session.targetName)}</strong><small>${safeText(session.protocol)} · ${safeText(session.state)} · ${safeText(session.recordingStatus || "INCOMPLETE")}</small><small>${formatTime(session.startedMs)}</small>`;
      item.onclick = () => openHistorySession(session); list.append(item);
    }
    if (!state.sessions.length) list.innerHTML = '<div class="muted">暂无已保存会话</div>';
    const pageSize = 20; const pageCount = Math.max(1, Math.ceil(state.total / pageSize));
    $("history-page").textContent = `第 ${state.historyPage} / ${pageCount} 页`;
    $("history-prev").disabled = state.historyPage <= 1; $("history-next").disabled = state.historyPage >= pageCount;
  }

  function createTerminal(tab) {
    const terminal = new Terminal({ convertEol:true, cursorBlink:true, scrollback:5000, fontSize:Number($("font-size").value), theme: state.dark ? { background:"#05080b" } : { background:"#fff", foreground:"#18212b" } });
    tab.fit = new FitAddon(); terminal.loadAddon(tab.fit); tab.search = new SearchAddon(); terminal.loadAddon(tab.search);
    terminal.onData(data => editInput(tab, data)); terminal.onResize(size => sendMessage(tab, { type:"resize", columns:size.cols, rows:size.rows })); return terminal;
  }
  function editInput(tab, data) {
    if (!tab.connected) return;
    if (data === "\u0003") { tab.inputBuffer = ""; tab.terminal.write("^C\r\n"); return sendMessage(tab, {type:"control",signal:"CTRL_C"}); }
    if (data === "\r" || data === "\n") { const command = tab.inputBuffer; tab.inputBuffer = ""; tab.terminal.write("\r\n"); if (command) sendMessage(tab, {type:"input",text:command}); return; }
    if (data === "\u007f" || data === "\b") { if (tab.inputBuffer.length) { tab.inputBuffer = tab.inputBuffer.slice(0, -1); tab.terminal.write("\b \b"); } return; }
    if (data.includes("\r") || data.includes("\n")) { tab.inputBuffer += data.replace(/[\r\n]/g, ""); tab.terminal.write(data.replace(/[\r\n]/g, "")); return; }
    if ([...data].some(character => character.charCodeAt(0) < 0x20)) return;
    tab.inputBuffer += data; tab.terminal.write(data);
  }
  async function createTab(device, protocol, serial = null) {
    const id = `tab-${crypto.randomUUID()}`; const tab = { id, label: serial?.serialPort ?? `${device.host} ${protocol}`, targetId: device.id, protocol, terminal:null, socket:null, connected:false, inputBuffer:"", serial };
    tab.terminal = createTerminal(tab); state.tabs.set(id, tab); state.active = id;
    const pane = document.createElement("div"); pane.className = "terminal-pane"; pane.dataset.pane = id; $("terminals").append(pane); tab.terminal.open(pane); tab.terminal.writeln(`\x1b[90mConnecting ${tab.label} (RESTRICTED_READONLY)...\x1b[0m`); renderTabs(); renderPanes();
    try { const session = await api("/api/system/terminal/sessions", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({ targetId:tab.targetId, protocol, mode:"RESTRICTED_READONLY", ...(serial || {}) }) }); tab.sessionId = session.sessionId; const ticket = await api(`/api/system/terminal/sessions/${tab.sessionId}/ticket`, { method:"POST" }); await openSocket(tab, ticket.ticket); }
    catch (error) { tab.terminal.writeln(`\r\n\x1b[31m${error.message}\x1b[0m`); notice(error.message, true); }
  }
  async function openSocket(tab, ticket) {
    const scheme = location.protocol === "https:" ? "wss" : "ws"; tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket)}`); tab.socket.binaryType = "arraybuffer";
    tab.socket.onopen = () => { tab.connected = true; fit(tab); renderTabs(); notice(`${tab.label} 已连接`); };
    tab.socket.onmessage = event => { if (typeof event.data === "string") { const message = JSON.parse(event.data); if (message.type === "error") tab.terminal.writeln(`\r\n\x1b[31m${message.error}\x1b[0m`); else if (message.type === "status") notice(`${tab.label}: ${message.session.state}`); } else tab.terminal.write(new Uint8Array(event.data)); };
    tab.socket.onclose = () => { tab.connected = false; renderTabs(); notice(`${tab.label} 已断开`); }; tab.socket.onerror = () => notice(`${tab.label} WebSocket 错误`, true);
  }
  function sendMessage(tab, value) { if (tab.socket?.readyState === WebSocket.OPEN) tab.socket.send(JSON.stringify(value)); }
  function fit(tab) { if (tab?.fit) { try { tab.fit.fit(); } catch {} } }
  function renderTabs() { const tabs = $("tabs"); tabs.replaceChildren(); if (!state.tabs.size) { tabs.innerHTML = '<span class="empty-tabs">暂无终端会话</span>'; return; } for (const tab of state.tabs.values()) { const item = document.createElement("div"); item.className = `tab${tab.id === state.active ? " active" : ""}`; item.innerHTML = `<span>${safeText(tab.label)} ${tab.connected ? "●" : "○"}</span><button title="关闭">×</button>`; item.onclick = () => { state.active = tab.id; renderTabs(); renderPanes(); }; item.querySelector("button").onclick = event => { event.stopPropagation(); closeTab(tab); }; tabs.append(item); } }
  function renderPanes() { const host = $("terminals"); host.classList.toggle("split", state.split); const tabs = [...state.tabs.values()]; const visible = new Set([state.active]); if (state.split) { const second = tabs.find(tab => tab.id !== state.active); if (second) visible.add(second.id); } for (const tab of tabs) { const pane = host.querySelector(`[data-pane="${tab.id}"]`); if (pane) pane.style.display = visible.has(tab.id) ? "block" : "none"; if (visible.has(tab.id)) fit(tab); } const welcome = host.querySelector(".welcome"); if (welcome) welcome.style.display = tabs.length ? "none" : "block"; }
  async function closeTab(tab) { try { if (tab.sessionId) await api(`/api/system/terminal/sessions/${tab.sessionId}/close`, {method:"POST"}); } catch {} tab.socket?.close(); tab.terminal?.dispose(); $("terminals").querySelector(`[data-pane="${tab.id}"]`)?.remove(); state.tabs.delete(tab.id); if (state.active === tab.id) state.active = state.tabs.keys().next().value || null; renderTabs(); renderPanes(); loadSessions(); }
  async function reconnect() { const tab = state.tabs.get(state.active); if (!tab) return; if (tab.socket) tab.socket.close(); try { const ticket = await api(`/api/system/terminal/sessions/${tab.sessionId}/ticket`, {method:"POST"}); await openSocket(tab, ticket.ticket); } catch (error) { notice(error.message, true); } }
  async function loadDevices() { try { state.devices = await api("/api/system/terminal/devices"); renderDevices(); $("service-state").textContent = `服务在线 · ${state.devices.length} 台登记设备`; } catch (error) { $("service-state").textContent = "服务未连接"; notice(error.message, true); } }
  async function loadSessions() {
    try {
      const params = new URLSearchParams({ page:String(state.historyPage), pageSize:"20" }); const device = $("history-device").value; const protocol = $("history-protocol").value; const integrity = $("history-integrity-filter").value; const from = $("history-from").value; const to = $("history-to").value; const keyword = $("history-keyword").value.trim();
      if (device) params.set("targetId", device); if (protocol) params.set("protocol", protocol); if (integrity) params.set("integrity", integrity); if (from) params.set("fromMs", String(new Date(from).getTime())); if (to) params.set("toMs", String(new Date(to).getTime())); if (keyword) params.set("keyword", keyword);
      const page = await api(`/api/system/terminal/sessions?${params}`); state.sessions = page.items || []; state.total = page.total || 0; renderSessions();
    } catch (error) { notice(error.message, true); }
  }
  async function openSerialDialog() { try { const ports = await api("/api/system/terminal/serial-ports"); const select = $("serial-port"); select.replaceChildren(...ports.map(port => new Option(port, port))); if (!ports.length) { notice("未发现本机 COM 端口", true); return; } $("serial-dialog").showModal(); } catch (error) { notice(error.message, true); } }

  function stopReplay() { if (state.history?.timer) clearTimeout(state.history.timer); if (state.history) state.history.playing = false; $("replay-play").textContent = "播放"; }
  function renderHistorySummary(session) {
    $("history-title").textContent = `${session.targetName} · ${session.protocol}`;
    $("history-summary").innerHTML = `<span>Session ID<br><strong>${safeText(session.id)}</strong><button id="history-copy">复制</button></span><span>状态<br>${safeText(session.state)} / ${safeText(session.recordingStatus)}</span><span>开始<br>${safeText(formatTime(session.startedMs))}</span><span>结束<br>${safeText(formatTime(session.endedMs))}</span><span>命令数<br>${session.commandCount ?? 0}</span><span>输出字节<br>${session.outputBytes ?? 0}</span><span>录制文件<br>${session.recordingBytes ?? 0} bytes</span><span>身份<br>${safeText(session.operatorIdentity || "UNATTRIBUTED")}</span>`;
    $("history-copy").onclick = async () => { try { await navigator.clipboard.writeText(session.id); notice("Session ID 已复制"); } catch { notice("复制失败", true); } };
  }
  function renderHistoryEvents() {
    const host = $("history-events"); host.replaceChildren(); const zone = $("history-timezone").value;
    for (const event of state.history.events) { const item = document.createElement("div"); item.className = "history-event"; item.innerHTML = `<strong>${safeText(event.eventType)}</strong><small>${safeText(formatTime(event.atMs, zone))}</small><small>${safeText(eventDetail(event.detailJson))}</small>`; item.onclick = () => seekToEvent(event.atMs); host.append(item); }
  }
  function writeReplayEntry(entry) { if (!state.history?.terminal) return; state.history.terminal.write(Uint8Array.from(atob(entry.dataB64), c => c.charCodeAt(0))); $("replay-progress").value = String(entry.position); }
  async function loadReplayPage() {
    const history = state.history;
    if (!history || !history.hasMore || history.loading) return false;
    history.loading = true;
    try {
      const page = await api(`/api/system/terminal/sessions/${history.session.id}/recording/replay?offset=${history.nextOffset}&limit=2000`);
      history.entries.push(...(page.entries || [])); history.nextOffset = history.entries.length; history.hasMore = Boolean(page.hasMore);
      $("replay-progress").max = String(Math.max(0, history.entries.length - 1));
      return (page.entries || []).length > 0;
    } finally { history.loading = false; }
  }
  async function loadAllReplayPages() {
    while (state.history?.hasMore) {
      const loaded = await loadReplayPage();
      if (!loaded) break;
    }
  }
  async function seekToEvent(atMs) {
    try {
      while (state.history?.hasMore && state.history.entries.at(-1)?.atMs < atMs) await loadReplayPage();
      seekReplay(state.history?.entries.findIndex(entry => entry.atMs >= atMs) ?? 0);
    } catch (error) { notice(error.message, true); }
  }
  async function playReplay() {
    if (!state.history) return; if (state.history.playing) { stopReplay(); return; }
    state.history.playing = true; $("replay-play").textContent = "暂停";
    const step = async () => {
      if (!state.history?.playing) return; const index = state.history.index;
      if (index >= state.history.entries.length) {
        if (state.history.hasMore) { const loaded = await loadReplayPage(); if (loaded && state.history.playing) return step(); }
        stopReplay(); return;
      }
      const entry = state.history.entries[index]; writeReplayEntry(entry); state.history.index++;
      const next = state.history.entries[state.history.index]; const speed = Number($("replay-speed").value); const delay = next ? Math.min(3000, Math.max(0, next.atMs - entry.atMs) / speed) : 0;
      state.history.timer = setTimeout(() => { step().catch(error => { stopReplay(); notice(error.message, true); }); }, delay);
    }; step().catch(error => { stopReplay(); notice(error.message, true); });
  }
  function seekReplay(index) {
    if (!state.history) return; stopReplay(); state.history.index = Math.max(0, Math.min(state.history.entries.length, index < 0 ? 0 : index)); state.history.terminal.reset(); for (let i = 0; i < state.history.index; i++) writeReplayEntry(state.history.entries[i]); $("replay-progress").value = String(Math.max(0, state.history.index - 1));
  }
  async function openHistorySession(item) {
    stopReplay();
    try {
      const session = await api(`/api/system/terminal/sessions/${item.id}`); const events = await api(`/api/system/terminal/sessions/${item.id}/events?limit=5000`); const integrity = await api(`/api/system/terminal/sessions/${item.id}/recording/integrity`); const replay = await api(`/api/system/terminal/sessions/${item.id}/recording/replay?limit=2000`);
      const terminal = new Terminal({ convertEol:true, disableStdin:true, cursorBlink:false, scrollback:10000, theme: state.dark ? { background:"#05080b" } : { background:"#fff", foreground:"#18212b" } }); const search = new SearchAddon(); terminal.loadAddon(search); terminal.open($("replay-terminal"));
      state.history = { session, events, integrity, entries:replay.entries || [], nextOffset:(replay.entries || []).length, hasMore:Boolean(replay.hasMore), loading:false, terminal, search, index:0, playing:false, timer:null };
      renderHistorySummary(session); $("history-integrity").textContent = `${integrity.status}: ${integrity.detail} · SHA-256 ${integrity.hashMatches ? "匹配" : "不匹配"}`; $("replay-progress").max = String(Math.max(0, state.history.entries.length - 1)); renderHistoryEvents(); $("history-dialog").showModal();
    } catch (error) { notice(error.message, true); }
  }
  async function exportHistory(format) {
    if (!state.history) return; try { const response = await fetch(`/api/system/terminal/sessions/${state.history.session.id}/export?format=${format}`, { headers:{ Authorization:`Bearer ${state.token}` } }); if (!response.ok) throw new Error(`${response.status}: ${await response.text()}`); const blob = await response.blob(); const link = document.createElement("a"); link.href = URL.createObjectURL(blob); link.download = `terminal-${state.history.session.id}.${format.toLowerCase()}`; link.click(); URL.revokeObjectURL(link.href); } catch (error) { notice(error.message, true); }
  }

  $("connect-token").onclick = async () => { state.token = $("token").value.trim(); sessionStorage.setItem("netconsole-token", state.token); await loadDevices(); await loadSessions(); };
  $("token").value = state.token; $("refresh-devices").onclick = loadDevices; $("refresh-sessions").onclick = loadSessions; $("split").onclick = () => { state.split = !state.split; renderPanes(); };
  $("disconnect").onclick = () => { const tab = state.tabs.get(state.active); if (tab) closeTab(tab); }; $("reconnect").onclick = reconnect;
  $("theme").onclick = () => { state.dark = !state.dark; document.documentElement.classList.toggle("light", !state.dark); $("theme").textContent = state.dark ? "浅色主题" : "深色主题"; for (const tab of state.tabs.values()) tab.terminal.options.theme = state.dark ? {background:"#05080b"} : {background:"#fff",foreground:"#18212b"}; if (state.history?.terminal) state.history.terminal.options.theme = state.dark ? {background:"#05080b"} : {background:"#fff",foreground:"#18212b"}; };
  $("font-size").onchange = event => { for (const tab of state.tabs.values()) tab.terminal.options.fontSize = Number(event.target.value); };
  $("search").onkeydown = event => { if (event.key !== "Enter") return; const tab = state.tabs.get(state.active); const query = event.target.value; if (tab?.search && query) tab.search.findNext(query); };
  $("serial-form").onsubmit = event => { if (event.submitter?.id !== "serial-open") return; const port = $("serial-port").value; createTab({id:`serial:${port}`,host:port}, "SERIAL", { serialPort:port, baudRate:Number($("baud").value) }); };
  const historyFilterChanged = () => { state.historyPage = 1; loadSessions(); }; $("history-device").onchange = historyFilterChanged; $("history-protocol").onchange = historyFilterChanged; $("history-integrity-filter").onchange = historyFilterChanged; $("history-from").onchange = historyFilterChanged; $("history-to").onchange = historyFilterChanged; $("history-keyword").onkeydown = event => { if (event.key === "Enter") historyFilterChanged(); };
  $("history-prev").onclick = () => { if (state.historyPage > 1) { state.historyPage--; loadSessions(); } }; $("history-next").onclick = () => { if (state.historyPage < Math.ceil(state.total / 20)) { state.historyPage++; loadSessions(); } };
  $("history-close").onclick = () => { stopReplay(); state.history?.terminal?.dispose(); state.history = null; $("history-dialog").close(); };
  $("history-timezone").onchange = renderHistoryEvents; $("replay-play").onclick = playReplay; $("replay-restart").onclick = () => seekReplay(0); $("replay-progress").oninput = event => seekReplay(Number(event.target.value)); $("replay-search").onkeydown = event => { if (event.key === "Enter" && state.history?.search) loadAllReplayPages().then(() => state.history?.search.findNext(event.target.value)).catch(error => notice(error.message, true)); };
  $("history-verify").onclick = async () => { if (!state.history) return; try { state.history.integrity = await api(`/api/system/terminal/sessions/${state.history.session.id}/recording/verify`, { method:"POST" }); $("history-integrity").textContent = `${state.history.integrity.status}: ${state.history.integrity.detail}`; await loadSessions(); } catch (error) { notice(error.message, true); } };
  document.querySelectorAll("[data-export]").forEach(button => button.onclick = () => exportHistory(button.dataset.export));
  renderDevices(); renderSessions(); if (state.token) { loadDevices(); loadSessions(); }
})();
