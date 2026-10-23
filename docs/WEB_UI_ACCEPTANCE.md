# Web PR-1 acceptance record

## Status vocabulary

- `IMPLEMENTED`: source and route exist and use a real Next API or an explicit disabled state.
- `CI_VERIFIED`: command completed successfully in CI/current workspace.
- `SIMULATED`: fixture or mocked browser evidence only.
- `PENDING_REAL_ENVIRONMENT`: requires Windows host, physical COM, LAN client, or real device.
- `NOT_STARTED`: later phase.

## Current evidence

| Check | Result | Evidence |
|---|---|---|
| Vue TypeScript compile | CI_VERIFIED | `cd web && npm run typecheck` |
| Vite static build | CI_VERIFIED | `cd web && npm run build` |
| Vitest | CI_VERIFIED | 2 tests passed |
| Web auth/CSRF lifecycle | CI_VERIFIED | `tests/web_auth_e2e.py`: refresh reuse, multi-tab, multi-browser isolation, valid post, logout, invalid CSRF, Origin and expiry negative paths |
| C# Release build | CI_VERIFIED | .NET 10 SDK installed in an isolated workspace directory; Release build passed |
| Existing C# regression | CI_VERIFIED | `tests/NetConsole.Next.Service.Tests` passed |
| Existing SSH.NET simulation | CI_VERIFIED | existing simulator/wire coverage passed |
| Vue terminal browser E2E | CI_VERIFIED / SIMULATED | real Service + synthetic SSH/Telnet/COM simulators passed `tests/terminal_e2e.py` |
| New Playwright browser screenshots | SIMULATED | Chromium rendered the built static UI against mocked API responses; screenshots are evidence of layout only |
| admin / superadmin auth | CI_VERIFIED | isolated real Service test passed login and role responses |
| multi-browser sessions | CI_VERIFIED | two independent cookie clients passed logout isolation |
| LAN bind | NOT_RUN | requires service and second client/interface |
| physical COM / H3C Comware | PENDING_REAL_ENVIRONMENT | local acceptance only |
| publish asset completeness | CI_VERIFIED | `dotnet publish` copied `wwwroot/index.html` and `wwwroot/app/assets`; external CDN/dev-path scan passed |
| Playwright dependency alignment | CI_VERIFIED locally | `playwright` and `@playwright/test` both locked to 1.64.1; CI installs the matching Chromium/headless shell |
| GitHub Linux / Windows jobs | PENDING | Awaiting the workflow run for the current fix commit |

## Local Windows acceptance conditions

Run Release build and the existing `tests/smoke.py`, `tests/terminal_e2e.py`, C# tests, then start a second browser from another LAN machine with `NETCONSOLE_NEXT_WEB_BIND=0.0.0.0`. Verify two independent admin sessions, cookie/CSRF writes, no token in browser storage, SSH/Telnet/COM on the host, recording integrity, replay/export, and that collection continues after all browsers close. Mark real H3C and physical COM separately; synthetic tests do not prove field compatibility.

## Fix report

```text
TASK=NETCONSOLE_NEXT_WEB_PR1_CSRF_CI_FIX
PR=17
BRANCH=codex/web-pr1-vue-ui
HEAD_BEFORE=fd883ac1060145fda99c176756a3a505895e7b4f
HEAD_AFTER=PENDING_COMMIT

CSRF_ROOT_CAUSE=/api/auth/csrf rotated the client Cookie without updating the session-bound csrf_hash.
CSRF_FIX=Reuse a valid session-bound token; atomically persist a new hash only when the presented token is absent/invalid; serialize concurrent refreshes and clear client state on logout/bootstrap failure.
CSRF_REFRESH=PASS
CSRF_MULTI_TAB=PASS
CSRF_MULTI_BROWSER=PASS
CSRF_NEGATIVE_TESTS=PASS

LINUX_PLAYWRIGHT_ROOT_CAUSE=CI CLI resolved @playwright/test 1.55.0 while browser E2E imported playwright 1.64.1.
LINUX_PLAYWRIGHT_FIX=Lock both packages to 1.64.1 and install Chromium with the matching CLI in both jobs.
LINUX_TERMINAL_E2E=PASS_LOCAL

WINDOWS_UNICODE_ROOT_CAUSE=Python subprocess text mode used the Windows locale and output writes assumed non-null strings.
WINDOWS_UNICODE_FIX=Explicit UTF-8 with replacement, safe stdout/stderr writes, preserved exit codes and failure output.
WINDOWS_TERMINAL_E2E=PASS_LOCAL_PENDING_GITHUB

VUE_TYPECHECK=PASS
VUE_BUILD=PASS
VITEST=PASS
DOTNET_BUILD=PASS
G1_RECOVERY=PASS
G23_TERMINAL_REGRESSION=PASS_SYNTHETIC
G24_COMPAT_REGRESSION=PASS_SYNTHETIC
WEB_AUTH_E2E=PASS
PLAYWRIGHT_E2E=PASS_LOCAL
PUBLISH_ASSETS=PASS

GITHUB_LINUX_CI=PENDING
GITHUB_WINDOWS_CI=PENDING
PR_CI_OVERALL=PENDING

LAN_REAL_CROSS_HOST=NOT_RUN
REAL_WINDOWS_HOST=NOT_RUN
REAL_H3C_DEVICE=NOT_RUN
PHYSICAL_COM=NOT_RUN

FINAL_STATUS=PARTIALLY_VERIFIED
```

