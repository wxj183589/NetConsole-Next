# Web UI migration matrix

| Area | Next PR-1 implementation | Source boundary | Status |
|---|---|---|---|
| App shell | `web/src/components/AppShell.vue` | Old layout interaction inspected; Next routes and tokens are new | IMPLEMENTED |
| Workspace tabs | `web/src/stores/workspace.ts` + AppShell | Old `WorkspaceTabBar` behavior used as functional reference | IMPLEMENTED |
| Theme | `web/src/styles/theme.css`, shell toggle | Old theme token idea; no old stylesheet copied | IMPLEMENTED |
| Device list | `web/src/views/DevicesView.vue` | Uses Next `/api/system/terminal/devices` and H3C profile API | IMPLEMENTED |
| Task center | `web/src/views/TasksView.vue` | Uses Next `/api/system/tasks` | IMPLEMENTED |
| Terminal | `web/src/views/TerminalView.vue` | Uses existing Next session/ticket/WebSocket/recording APIs | IMPLEMENTED |
| Login | `WebAuthStore`, `/api/auth/*` | New Next service capability | IMPLEMENTED |
| Wireless / rail / tools / files | Disabled route placeholders | No fake data; contracts deferred | NOT_STARTED |
| Host desktop helper | Not part of PR-1 | Design only in `HOST_TOOL_AND_FILE_DESIGN.md` | NOT_STARTED |
| SignalR real-time | Not part of PR-1 | Design only; polling is not duplicated per page | NOT_STARTED |
| MR/MESH timeline | Not part of PR-1 | Public state contract in `UNIFIED_TIMELINE_DESIGN.md` | NOT_STARTED |

## Reuse inventory

Reviewed from old NetConsole: `AppLayout.vue`, `NcLayout.vue`, `WorkspaceTabBar.vue`, `DeviceManagementView.vue`, `GlobalTaskCenter.vue`, `platform/runtime.ts`, `api/client.ts`, theme tokens, MESH analysis components, rail timeline controller and online MR components. The new app re-implements only the interaction patterns needed by actual Next APIs and leaves analysis rules in the old project as reference material.

