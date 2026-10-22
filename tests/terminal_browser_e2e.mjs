import { chromium } from "../web/node_modules/playwright/index.mjs";

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL;
const token = process.env.NETCONSOLE_E2E_TOKEN;
const sshPort = process.env.NETCONSOLE_E2E_SSH_PORT;
const telnetPort = process.env.NETCONSOLE_E2E_TELNET_PORT;
if (!baseUrl || !token || !sshPort || !telnetPort) throw new Error("E2E base URL, token and simulator ports are required");

const executablePath = process.env.NETCONSOLE_E2E_BROWSER;
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ["--no-sandbox"] });
const page = await browser.newPage({ permissions: ["clipboard-read", "clipboard-write"] });
await page.addInitScript(() => {
  window.__terminalMessages = [];
  const originalSend = WebSocket.prototype.send;
  WebSocket.prototype.send = function (value) {
    try { window.__terminalMessages.push(typeof value === "string" ? JSON.parse(value) : { binary: true, length: value.byteLength }); } catch { }
    return originalSend.call(this, value);
  };
});
const consoleErrors = [];
const failedRequests = [];
const responses = [];
page.on("console", message => {
  const expectedTicketRejection = message.text().includes("sessions/missing/stream?ticket=invalid");
  if (message.type() === "error" && !expectedTicketRejection && !message.text().includes("401 (Unauthorized)")) consoleErrors.push(message.text());
});
page.on("requestfailed", request => {
  const expectedUnauthorizedApi = request.url().endsWith("/api/system/terminal/devices");
  if (!expectedUnauthorizedApi) failedRequests.push(`${request.url()} ${request.failure()?.errorText ?? "failed"}`);
});
page.on("response", response => { if (response.url().includes("/api/system/terminal")) responses.push(`${response.status()} ${response.url()}`); });
page.on("request", request => { if (request.url().includes("cdn.jsdelivr.net")) throw new Error(`CDN request: ${request.url()}`); });
const waitForRows = async value => page.waitForFunction(text => [...document.querySelectorAll(".xterm-rows")].some(rows => rows.innerText.includes(text)), value, { timeout: 10000 });

try {
  await page.goto(`${baseUrl}/`, { waitUntil: "networkidle" });
  await page.locator("#token").fill(token);
  await page.locator("#connect-token").click();
  await page.locator("#service-state").filter({ hasText: "服务在线" }).waitFor();
  if (await page.locator("#device-list .device").count() !== 2) throw new Error("expected two registered devices");
  const noAuth = await page.evaluate(url => fetch(url).then(response => response.status), `${baseUrl}/api/system/terminal/devices`);
  if (noAuth !== 401) throw new Error(`unauthorized API status was ${noAuth}`);

  const sshDevice = page.locator("#device-list .device").filter({ hasText: `:${sshPort}` });
  const telnetDevice = page.locator("#device-list .device").filter({ hasText: `:${telnetPort}` });
  await sshDevice.getByRole("button", { name: "SSH" }).click();
  await page.locator("#tabs .tab").filter({ hasText: "SSH" }).waitFor();
  await waitForRows("SSH synthetic ready");
  await page.locator(".terminal-pane").first().locator(".xterm-helper-textarea").focus();
  await page.keyboard.insertText("display interface brief");
  await page.keyboard.press("Enter");
  await waitForRows("simulated-ssh:display interface brief");

  await telnetDevice.getByRole("button", { name: "TELNET" }).click();
  await page.locator("#tabs .tab").filter({ hasText: "TELNET" }).waitFor();
  await waitForRows("TELNET synthetic ready");
  const activePane = page.locator(".terminal-pane:visible").last();
  await activePane.click();
  await activePane.locator(".xterm-helper-textarea").focus();
  await page.keyboard.insertText("display interface brief");
  await page.keyboard.press("Enter");
  await waitForRows("simulated-telnet:display interface brief");

  await page.getByRole("button", { name: "分屏" }).click();
  if (await page.locator("#terminals.split .terminal-pane").count() !== 2) throw new Error("split view did not show two panes");
  await page.locator("#search").fill("synthetic");
  await page.locator("#search").press("Enter");
  await page.getByRole("button", { name: "浅色主题" }).click();
  if (await page.locator("html.light").count() !== 1) throw new Error("theme toggle did not apply");

  await page.getByRole("button", { name: "打开本地 COM Console" }).click();
  await page.locator("#serial-dialog").waitFor({ state: "visible" });
  if (await page.locator("#serial-port option[value='SIMULATED-COM1']").count() !== 1) throw new Error("simulated COM missing");
  await page.locator("#serial-port").selectOption("SIMULATED-COM1");
  await page.locator("#serial-open").click();
  await page.locator("#tabs .tab").filter({ hasText: "SIMULATED-COM1" }).waitFor();
  await waitForRows("simulated console ready");

  const invalidTicket = await page.evaluate(url => new Promise(resolve => {
    const socket = new WebSocket(url);
    const timer = setTimeout(() => { socket.close(); resolve("timeout"); }, 3000);
    socket.onopen = () => { clearTimeout(timer); socket.close(); resolve("opened"); };
    socket.onerror = () => { clearTimeout(timer); resolve("rejected"); };
    socket.onclose = () => { clearTimeout(timer); resolve("rejected"); };
  }), `${baseUrl.replace("http", "ws")}/api/system/terminal/sessions/missing/stream?ticket=invalid`);
  if (invalidTicket !== "rejected") throw new Error(`invalid ticket was ${invalidTicket}`);

  await page.getByRole("button", { name: "断开" }).click();
  await page.reload({ waitUntil: "networkidle" });
  if (await page.locator("#session-list .session").count() < 1) throw new Error("session history did not survive refresh");
  if (consoleErrors.length || failedRequests.length) throw new Error(`browser diagnostics: console=${consoleErrors.join(" | ")} failed=${failedRequests.join(" | ")}`);
  console.log("PASS: browser device list, SSH/Telnet WebSocket, simulated COM, split/search/theme, ticket rejection and refresh history");
} catch (error) {
  const diagnostics = await page.evaluate(async () => ({
    serviceState: document.querySelector("#service-state")?.textContent,
    notice: document.querySelector("#notice")?.textContent,
    tabs: document.querySelector("#tabs")?.innerText,
    terminals: document.querySelector("#terminals")?.innerText,
    sessions: document.querySelector("#session-list")?.innerText,
    terminalMessages: window.__terminalMessages,
    sessionApi: await fetch("/api/system/terminal/sessions", { headers: { Authorization: `Bearer ${sessionStorage.getItem("netconsole-token")}` } }).then(async response => {
      const body = await response.json();
      const details = await Promise.all(body.map(async session => ({
        session: await fetch(`/api/system/terminal/sessions/${session.id}`, { headers: { Authorization: `Bearer ${sessionStorage.getItem("netconsole-token")}` } }).then(detail => detail.json()),
        events: await fetch(`/api/system/terminal/sessions/${session.id}/events`, { headers: { Authorization: `Bearer ${sessionStorage.getItem("netconsole-token")}` } }).then(events => events.json())
      })));
      return { status: response.status, body, details };
    })
  })).catch(() => ({}));
  console.error(`E2E diagnostics: ${JSON.stringify({ error: String(error), diagnostics, consoleErrors, failedRequests, responses })}`);
  throw error;
} finally {
  await browser.close();
}
