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

## Local Windows acceptance conditions

Run Release build and the existing `tests/smoke.py`, `tests/terminal_e2e.py`, C# tests, then start a second browser from another LAN machine with `NETCONSOLE_NEXT_WEB_BIND=0.0.0.0`. Verify two independent admin sessions, cookie/CSRF writes, no token in browser storage, SSH/Telnet/COM on the host, recording integrity, replay/export, and that collection continues after all browsers close. Mark real H3C and physical COM separately; synthetic tests do not prove field compatibility.

