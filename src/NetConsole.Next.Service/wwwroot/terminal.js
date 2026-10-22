import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { SearchAddon } from "@xterm/addon-search";

(() => {
  "use strict";
  const state = { token: sessionStorage.getItem("netconsole-token") || "", devices: [], sessions: [], tabs: new Map(), active: null, split: false, dark: true };
  const $ = id => document.getElementById(id);
  const notice = (text, error = false) => { $("notice").textContent = text || ""; $("notice").classList.toggle("error", error); };
  const api = async (path, options = {}) => {
    const headers = new Headers(options.headers || {}); if (state.token) headers.set("Authorization", `Bearer ${state.token}`);
    const response = await fetch(path, { ...options, headers });
    if (!response.ok) { const body = await response.text(); throw new Error(`${response.status}: ${body || response.statusText}`); }
    return response.status === 204 ? null : response.json();
  };
  const safeText = value => String(value ?? "").replace(/[&<>\"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
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
  }
  function renderSessions() {
    const list = $("session-list"); list.replaceChildren();
    for (const session of state.sessions.slice(0, 20)) { const item = document.createElement("div"); item.className = "session"; item.innerHTML = `<strong>${safeText(session.targetName)}</strong><small>${safeText(session.protocol)} · ${safeText(session.state)} · ${new Date(session.startedMs).toLocaleString()}</small>`; list.append(item); }
    if (!state.sessions.length) list.innerHTML = '<div class="muted">暂无已保存会话</div>';
  }
  function createTerminal(tab) {
    const terminal = new Terminal({ convertEol:true, cursorBlink:true, scrollback:5000, fontSize:Number($("font-size").value), theme: state.dark ? { background:"#05080b" } : { background:"#fff", foreground:"#18212b" } });
    tab.fit = new FitAddon(); terminal.loadAddon(tab.fit);
    tab.search = new SearchAddon(); terminal.loadAddon(tab.search);
    terminal.onData(data => editInput(tab, data)); terminal.onResize(size => sendMessage(tab, { type:"resize", columns:size.cols, rows:size.rows }));
    return terminal;
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
    tab.terminal = createTerminal(tab); if (!tab.terminal) return; state.tabs.set(id, tab); state.active = id;
    const pane = document.createElement("div"); pane.className = "terminal-pane"; pane.dataset.pane = id; $("terminals").append(pane); tab.terminal.open(pane); tab.terminal.writeln(`\x1b[90mConnecting ${tab.label} (RESTRICTED_READONLY)...\x1b[0m`);
    renderTabs(); renderPanes();
    try { const session = await api("/api/system/terminal/sessions", { method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({ targetId:tab.targetId, protocol, mode:"RESTRICTED_READONLY", ...(serial || {}) }) }); tab.sessionId = session.sessionId; const ticket = await api(`/api/system/terminal/sessions/${tab.sessionId}/ticket`, { method:"POST" }); await openSocket(tab, ticket.ticket); }
    catch (error) { tab.terminal.writeln(`\r\n\x1b[31m${error.message}\x1b[0m`); notice(error.message, true); }
  }
  async function openSocket(tab, ticket) {
    const scheme = location.protocol === "https:" ? "wss" : "ws"; tab.socket = new WebSocket(`${scheme}://${location.host}/api/system/terminal/sessions/${tab.sessionId}/stream?ticket=${encodeURIComponent(ticket)}`);
    tab.socket.binaryType = "arraybuffer"; tab.socket.onopen = () => { tab.connected = true; fit(tab); notice(`${tab.label} 已连接`); };
    tab.socket.onmessage = event => { if (typeof event.data === "string") { const message = JSON.parse(event.data); if (message.type === "error") tab.terminal.writeln(`\r\n\x1b[31m${message.error}\x1b[0m`); else if (message.type === "status") notice(`${tab.label}: ${message.session.state}`); } else tab.terminal.write(new Uint8Array(event.data)); };
    tab.socket.onclose = () => { tab.connected = false; renderTabs(); notice(`${tab.label} 已断开`); };
    tab.socket.onerror = () => notice(`${tab.label} WebSocket 错误`, true);
  }
  function sendMessage(tab, value) { if (tab.socket?.readyState === WebSocket.OPEN) tab.socket.send(JSON.stringify(value)); }
  function fit(tab) { if (tab?.fit) { try { tab.fit.fit(); } catch {} } }
  function renderTabs() { const tabs = $("tabs"); tabs.replaceChildren(); if (!state.tabs.size) { tabs.innerHTML = '<span class="empty-tabs">暂无终端会话</span>'; return; } for (const tab of state.tabs.values()) { const item = document.createElement("div"); item.className = `tab${tab.id === state.active ? " active" : ""}`; item.innerHTML = `<span>${safeText(tab.label)} ${tab.connected ? "●" : "○"}</span><button title="关闭">×</button>`; item.onclick = () => { state.active = tab.id; renderTabs(); renderPanes(); }; item.querySelector("button").onclick = event => { event.stopPropagation(); closeTab(tab); }; tabs.append(item); } }
  function renderPanes() { const host = $("terminals"); host.classList.toggle("split", state.split); const tabs = [...state.tabs.values()]; const visible = new Set([state.active]); if (state.split) { const second = tabs.find(tab => tab.id !== state.active); if (second) visible.add(second.id); } for (const tab of tabs) { const pane = host.querySelector(`[data-pane="${tab.id}"]`); if (pane) pane.style.display = visible.has(tab.id) ? "block" : "none"; if (visible.has(tab.id)) fit(tab); } const welcome = host.querySelector(".welcome"); if (welcome) welcome.style.display = tabs.length ? "none" : "block"; }
  async function closeTab(tab) { try { if (tab.sessionId) await api(`/api/system/terminal/sessions/${tab.sessionId}/close`, {method:"POST"}); } catch {} tab.socket?.close(); tab.terminal?.dispose(); $("terminals").querySelector(`[data-pane="${tab.id}"]`)?.remove(); state.tabs.delete(tab.id); if (state.active === tab.id) state.active = state.tabs.keys().next().value || null; renderTabs(); renderPanes(); loadSessions(); }
  async function reconnect() { const tab = state.tabs.get(state.active); if (!tab) return; if (tab.socket) tab.socket.close(); try { const ticket = await api(`/api/system/terminal/sessions/${tab.sessionId}/ticket`, {method:"POST"}); await openSocket(tab, ticket.ticket); } catch (error) { notice(error.message, true); } }
  async function loadDevices() { try { state.devices = await api("/api/system/terminal/devices"); renderDevices(); $("service-state").textContent = `服务在线 · ${state.devices.length} 台登记设备`; } catch (error) { $("service-state").textContent = "服务未连接"; notice(error.message, true); } }
  async function loadSessions() { try { state.sessions = await api("/api/system/terminal/sessions"); renderSessions(); } catch {} }
  async function openSerialDialog() { try { const ports = await api("/api/system/terminal/serial-ports"); const select = $("serial-port"); select.replaceChildren(...ports.map(port => new Option(port, port))); if (!ports.length) { notice("未发现本机 COM 端口", true); return; } $("serial-dialog").showModal(); } catch (error) { notice(error.message, true); } }
  $("connect-token").onclick = async () => { state.token = $("token").value.trim(); sessionStorage.setItem("netconsole-token", state.token); await loadDevices(); await loadSessions(); };
  $("token").value = state.token; $("refresh-devices").onclick = loadDevices; $("refresh-sessions").onclick = loadSessions; $("split").onclick = () => { state.split = !state.split; renderPanes(); };
  $("disconnect").onclick = () => { const tab = state.tabs.get(state.active); if (tab) closeTab(tab); }; $("reconnect").onclick = reconnect;
  $("theme").onclick = () => { state.dark = !state.dark; document.documentElement.classList.toggle("light", !state.dark); $("theme").textContent = state.dark ? "浅色主题" : "深色主题"; for (const tab of state.tabs.values()) tab.terminal.options.theme = state.dark ? {background:"#05080b"} : {background:"#fff",foreground:"#18212b"}; };
  $("font-size").onchange = event => { for (const tab of state.tabs.values()) tab.terminal.options.fontSize = Number(event.target.value); };
  $("search").onkeydown = event => { if (event.key !== "Enter") return; const tab = state.tabs.get(state.active); const query = event.target.value; if (tab?.search && query) tab.search.findNext(query); };
  $("serial-form").onsubmit = event => { if (event.submitter?.id !== "serial-open") return; const port = $("serial-port").value; createTab({id:`serial:${port}`,host:port}, "SERIAL", { serialPort:port, baudRate:Number($("baud").value) }); };
  renderDevices(); renderSessions(); if (state.token) { loadDevices(); loadSessions(); }
})();
