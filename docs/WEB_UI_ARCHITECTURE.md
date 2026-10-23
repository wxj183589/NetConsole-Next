# NetConsole Next Web UI Architecture

Status: `IMPLEMENTED` for Web PR-1 source; `CI_VERIFIED` is reported only for checks that ran in the current environment.

## Boundary and references

- `PRIMARY_PROJECT=NETCONSOLE_NEXT`: the Vue application, API client, and service changes live in this repository.
- `ART_DESIGN_PRO_USAGE=VISUAL_REFERENCE_ONLY`: Art Design Pro was inspected for compact sidebar/header/tabs, breadcrumbs, theme tokens, table density, status pills, loading and empty states. No Art Design Pro route, store, permission model, page, or project scaffold was copied.
- `OLD_NETCONSOLE_USAGE=FUNCTIONAL_REFERENCE_ONLY`: old `apps/desktop_renderer` was inspected for layout, workspace tabs, device workflows, terminal history, themes, and MESH timeline conventions. No old renderer application was mounted or copied wholesale.

The Next-specific design system is in `web/src/styles/theme.css`. It keeps a dark industrial sidebar, blue action color, dense cards/tables, explicit disabled states, and an offline-first asset build.

## Runtime shape

```text
Browser (Vue 3 + TypeScript)
  ├─ Pinia auth/workspace state
  ├─ Vue Router guarded routes
  ├─ Element Plus controls and dialogs
  ├─ ECharts snapshot chart (PR-3 will replace polling with SignalR)
  └─ xterm.js terminal over the existing ticketed WebSocket
          │ same-origin HTTP / WebSocket
NetConsole.Next.Service (.NET 10)
  ├─ existing Catalog / SQLite / Raw / scheduler / terminal core
  ├─ WebAuthStore: PBKDF2 users + independent Cookie sessions
  └─ static files from wwwroot (no Node.js or CDN at runtime)
```

The browser uses `netconsole_session` (HttpOnly, SameSite=Lax) and a separate CSRF token cookie/header pair. The legacy LocalManager bearer token remains accepted for local automation and is not requested, stored, or rendered by the Vue UI.

## Navigation and state

The primary routes are: overview, devices, terminal, tasks, wireless, rail, tools, files, and settings. Routes without a Next backend capability are visible but disabled and explicitly labeled `开发中`; they do not render fixture business records. Workspace tabs are independent and persisted in `sessionStorage`; route components are kept alive for the current browser tab.

## Web PR-1 scope

`IMPLEMENTED`:

- login for seeded `admin` / `superadmin` identities;
- independent browser sessions, PBKDF2 password hashes, password change, role-bearing identity;
- explicit loopback default and opt-in LAN bind (`NETCONSOLE_NEXT_WEB_BIND=0.0.0.0` or `NETCONSOLE_NEXT_WEB_LAN=1`);
- origin + CSRF checks for Cookie-authenticated writes;
- Next-specific layout, light/dark theme, workspace tabs, real health/device/task APIs;
- Vue xterm terminal for SSH/Telnet/COM, multiple tabs, split view, search, history, read-only replay and TXT/JSONL/HTML export;
- deterministic unit tests and offline Vite output under Service `wwwroot`.

`PENDING_REAL_ENVIRONMENT`: Windows Service installation, cross-computer LAN access, physical COM, real Comware device, screenshot evidence against a running .NET service, and production host acceptance.

