# NetConsole Next Web UI Architecture

Status: IMPLEMENTED for Web PR-1 and PR-2 source; CI_VERIFIED is reported only for checks that actually ran.

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
  ├─ HostToolRegistry + DesktopHelperIpc: SUPERADMIN-only host GUI boundary
  ├─ FileManagerService + owner-bound background ZIP archive jobs
  └─ static files from wwwroot (no Node.js or CDN at runtime)
```

The browser uses `netconsole_session` (HttpOnly, SameSite=Lax) and a separate CSRF token cookie/header pair. The legacy LocalManager bearer token remains accepted for local automation and is not requested, stored, or rendered by the Vue UI.

## Navigation and state

The primary routes are: overview, devices, terminal, tasks, wireless, rail, tools, files, and settings. PR-2 activates tools and files against real service APIs; wireless and rail remain explicitly disabled and do not render fixture business records. Workspace tabs are independent and persisted in sessionStorage; route components are kept alive for the current browser tab.

## Web PR-1 scope

`IMPLEMENTED`:

- login for seeded `admin` / `superadmin` identities;
- independent browser sessions, PBKDF2 password hashes, password change, role-bearing identity;
- explicit loopback default and opt-in LAN bind (`NETCONSOLE_NEXT_WEB_BIND=0.0.0.0` or `NETCONSOLE_NEXT_WEB_LAN=1`);
- origin + CSRF checks for Cookie-authenticated writes;
- Next-specific layout, light/dark theme, workspace tabs, real health/device/task APIs;
- Vue xterm terminal for SSH/Telnet/COM, multiple tabs, split view, search, history, read-only replay and TXT/JSONL/HTML export;
- deterministic unit tests and offline Vite output under Service `wwwroot`.

PENDING_REAL_ENVIRONMENT: Windows Service installation, cross-computer LAN access, physical COM, real Comware device, screenshot evidence against a running .NET service, and production host acceptance.

## Web PR-2 boundary

- /tools reads the Next host-tool registry. Only SUPERADMIN can configure a host executable or request a launch. ADMIN receives 403.
- /files uses logical roots and server-side path resolution. It never receives the data-root absolute path.
- Single-file download is streamed by the existing service; ZIP jobs are in-memory metadata plus a temp ZIP under the isolated data root and are bound to the creating user/session.
- Password recovery is deliberately not a Web route. The elevated Local Manager command and the two tools/ wrappers update only the user hash, revoke that user’s sessions/CSRF tokens, and preserve catalog/raw/credentials.

