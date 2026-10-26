# Device Management Phase 2-A Design

## Scope

P2-A delivers a server-owned, persistent batch operation boundary for device inventory maintenance. It covers:

- `BATCH_ENABLE` / `BATCH_DISABLE`
- `BATCH_SET_GROUP` / `BATCH_SET_SITE`
- `BATCH_UPDATE_METADATA`
- explicit target IDs, preview, confirmation, cancellation and safe retry
- optimistic version checks, atomic commit, bounded resources and structured audit
- Web UI selection and operation recovery after refresh

P2-A does not implement address/VLAN maintenance, H3C capability/task orchestration, CSV redesign, or rail-specific reference data. Those remain P2-B through P2-E.

## Boundary

The Web API accepts a request only after authentication, CSRF validation and `SUPERADMIN` authorization. The Catalog owns normalization, preview snapshots, operation persistence, version validation and the SQLite transaction. The browser never writes device rows directly and never treats a preview as a commit.

## API

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/system/devices/batch` | Create or idempotently reload a preview |
| GET | `/api/system/device-operations/{id}` | Read an owner-scoped operation |
| POST | `/api/system/devices/batch/{id}/confirm` | Explicitly confirm and atomically commit |
| POST | `/api/system/devices/batch/{id}/cancel` | Cancel a pending or failed operation |

The request contains `kind`, `idempotencyKey` and an explicit `deviceIds` array. Group/site/metadata fields are accepted only for the corresponding operation kind. The response includes the operation state, per-item outcome, expected revision and safe detail code.

## Persistence

Schema v12 adds `device_operations` and `device_operation_items`. An operation owns its immutable request and per-device before/requested snapshots. `expected_revision` is the `device_inventory.updated_ms` observed at preview time. Audit rows use the existing `audit_events` table and contain actor, correlation ID, safe changed-field names and a bounded failure code; credentials and command text are not copied.

## Commit rule

Confirmation re-reads every target in one serializable SQLite transaction. It rejects a missing target, changed `updated_ms`, invalid site/group scope or management-IP conflict. Any rejection rolls back the entire batch and marks the operation `CONFLICT`; no selected device is partially committed.

## Resource boundary

The implementation bounds each request to 100 IDs and 256 KiB, limits pending operations globally and per actor, limits running operations, caps audit payloads and removes old completed rows after the bounded retention threshold. These limits return a safe 429 response and do not expose database details.
