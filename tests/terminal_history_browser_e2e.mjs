import { chromium } from "../web/node_modules/playwright/index.mjs";

const baseUrl = process.env.NETCONSOLE_E2E_BASE_URL;
const token = process.env.NETCONSOLE_E2E_TOKEN;
if (!baseUrl || !token) throw new Error("history E2E base URL and token are required");
const executablePath = process.env.NETCONSOLE_E2E_BROWSER;
const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}), args: ["--no-sandbox"] });
const page = await browser.newPage();
const errors = [];
page.on("console", message => { if (message.type() === "error" && !message.text().includes("401")) errors.push(message.text()); });
page.on("request", request => { if (request.url().includes("cdn.jsdelivr.net")) errors.push(`CDN request: ${request.url()}`); });

try {
  await page.goto(`${baseUrl}/`, { waitUntil: "networkidle" });
  await page.locator("#token").fill(token);
  await page.locator("#connect-token").click();
  await page.locator("#service-state").filter({ hasText: "服务在线" }).waitFor();
  await page.locator("#session-list .session").first().waitFor({ timeout: 10000 });
  await page.locator("#session-list .session").first().click();
  await page.locator("#history-dialog").waitFor({ state: "visible" });
  await page.locator("#history-events .history-event").first().waitFor();
  const integrity = await page.locator("#history-dialog #history-integrity").innerText();
  if (!/(COMPLETE|INCOMPLETE|INTERRUPTED|MISSING|CORRUPTED)/.test(integrity)) throw new Error(`missing integrity status: ${integrity}`);
  if (await page.locator("#replay-terminal .xterm-rows").count() !== 1) throw new Error("replay xterm did not open");

  await page.getByRole("button", { name: "播放" }).click();
  await page.getByRole("button", { name: "暂停" }).click();
  await page.getByRole("button", { name: "重新开始" }).click();
  await page.locator("#replay-speed").selectOption("2");
  await page.locator("#replay-progress").fill("0");
  await page.locator("#replay-search").fill("synthetic");
  await page.locator("#replay-search").press("Enter");
  await page.locator("#history-timezone").selectOption("UTC");
  await page.locator("#history-timezone").selectOption("Asia/Shanghai");
  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: "JSONL" }).click();
  const download = await downloadPromise;
  if (!(await download.suggestedFilename()).endsWith(".jsonl")) throw new Error("JSONL export filename is wrong");
  await page.getByRole("button", { name: "关闭" }).click();
  if (await page.locator("#history-dialog").isVisible()) throw new Error("history dialog did not close");
  if (errors.length) throw new Error(`browser diagnostics: ${errors.join(" | ")}`);
  console.log("PASS: browser session history, UTC/Asia-Shanghai timeline, read-only replay controls and JSONL export");
} catch (error) {
  console.error(`history browser E2E diagnostics: ${String(error)}; integrity=${await page.locator("#history-dialog #history-integrity").innerText().catch(() => "")}`);
  throw error;
} finally {
  await browser.close();
}
