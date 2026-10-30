# Device P2-A Browser Diagnostics and Visual Acceptance

## Scope and command

`tests/device_p2a_browser_diagnostics.mjs` runs Chromium/Chrome against the real Release .NET Service on loopback. It uses a newly created isolated data root, seeds six local fixture devices and one group through the bearer API, and exercises the UI rather than mocking the batch endpoints.

```text
$env:NETCONSOLE_E2E_BASE_URL='http://127.0.0.1:18774'
$env:NETCONSOLE_E2E_DATA_ROOT='D:\study\NetConsole-Next-Workspace\diagnostic\p2a-browser-data'
$env:NETCONSOLE_P2A_BROWSER_OUTPUT='D:\study\NetConsole-Next-Workspace\build\device-p2a-browser'
$env:CHROME_PATH='C:\Program Files\Google\Chrome\Application\chrome.exe'
$env:NETCONSOLE_P2A_GIT_SHA=(git rev-parse HEAD)
node tests/device_p2a_browser_diagnostics.mjs
```

The temporary data root and service logs are retired after the run. The screenshots and `browser-evidence.json` are small, reviewable acceptance artifacts kept under `D:\study\NetConsole-Next-Workspace\build\device-p2a-browser`.

## Diagnostics policy

The script records browser `console` errors, `pageerror`, `requestfailed`, unexpected HTTP 500+ and unmatched 4xx, WebSocket socket errors, and unhandled promise rejections. The explicit whitelist covers only expected boundary behavior exercised by this test:

- unauthenticated `GET /api/auth/me` → 401;
- unavailable terminal inventory `GET /api/system/terminal/devices` → 404;
- optimistic batch conflict confirm → 409;
- admin device write denial → 403.

Expected HTTP failures are not silently treated as generic success: the endpoint, method, status and pattern are stored in the evidence JSON. Any other console error, failed request, page error, HTTP error, WebSocket error or unhandled rejection fails the run.

## Browser flows

- light theme at 1920×1080 and dark theme at 1440×900;
- device-management layout and dialog viewport checks at 1366×768;
- all five batch kinds: enable, disable, group, site and metadata;
- preview, confirm, reload recovery, cancel, optimistic conflict, failed-operation reload and safe retry;
- admin control isolation, expected 403, logout, relogin, and final viewport checks.

## Evidence

The final successful run must contain:

- `overview-light.png`;
- `overview-dark.png`;
- `batch-preview.png`;
- `batch-recovery.png`;
- `batch-cancel.png`;
- `batch-conflict.png`;
- `browser-evidence.json` with `passed=true`, the final P2-A Git SHA, all three viewport sizes, and zero unexpected diagnostics.

The artifacts are evidence for the isolated fixture only. They are not evidence of real-device access or production-data compatibility.
