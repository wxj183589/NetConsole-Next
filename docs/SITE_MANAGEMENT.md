# NetConsole Next 全局局点管理

Status: IMPLEMENTED for the global Site management vertical slice. The service and Web UI are authoritative for the local catalog only; no Production or legacy NetConsole data directory is imported.

## Model

`sites.id` is a stable, normalized identifier. It is created once and cannot be renamed. `name` and `description` are editable display metadata. `status` is one of `ACTIVE`, `DISABLED`, or `ARCHIVED`; status changes are soft lifecycle transitions and never hard-delete devices, groups, collection sources, terminal profiles, or history.

The seeded `local` Site remains `ACTIVE` for compatibility with the existing local catalog bridge. Device creation, import, group assignment, and batch Site changes accept only `ACTIVE` Sites.

Schema migration v16 adds the lifecycle metadata and status index to the existing `sites` table. Existing v1 catalogs are upgraded in place and receive bounded timestamps during startup. The migration is embedded in the service assembly and is not a data-root copy or migration from the old NetConsole project.

## API boundary

- `GET /api/system/sites` resolves the existing P2B5 Site Scope before returning Sites and relationship counts.
- `POST /api/system/sites`, `PUT /api/system/sites/{id}`, and `POST /api/system/sites/{id}/status` are SUPERADMIN-only and use `updatedMs` optimistic concurrency when supplied.
- Device inventory, device groups, H3C terminal profiles, terminal devices, terminal session lists, session detail, recording, replay, export, ticket, close, and WebSocket stream all use the same Site Scope boundary.
- An ADMIN without a trusted active membership authority remains denied. The Web UI does not synthesize membership claims or fall back to a guessed Site.

## Web workbench

The shell loads the Site list once after authentication and persists only the selected stable ID in browser storage. Devices and Terminal reload against the selected active Site. The shell shows the current Site and a normal/compact density switch. The Terminal workbench keeps the device inspector and history dock collapsible so the console can use the available width.

The `/sites` route is visible to SUPERADMIN and provides create, edit, disable, re-enable, and archive actions. There is deliberately no delete action: relationship counts and stable identity make lifecycle transitions auditable and reversible where appropriate.

## Verification boundary

Automated verification covers service compilation, the existing service contract suite, Web type checking, Web unit tests, and Vite production build. Browser screenshots and real device acceptance remain environment checks and must be reported separately from automated success.
