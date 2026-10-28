# Device Management P2-B API Design

## API principles

The API is a design contract only in this branch. It must preserve v12 device identity, expose provenance, require explicit scope, use optimistic concurrency and return bounded results. All write endpoints use the existing authentication, role, CSRF, request-size, audit and redaction boundaries.

## Read endpoints

| Endpoint | Purpose | Required query controls |
|---|---|---|
| `GET /api/system/devices/{id}/addresses` | Address rows plus effective projection | domain, source, family, lifecycle, cursor, limit |
| `GET /api/system/devices/{id}/network-memberships` | Device/domain membership and conflicts | site/domain, lifecycle, cursor, limit |
| `GET /api/system/network-domains` | Bounded domain inventory | site/station/line/kind, cursor, limit |
| `GET /api/system/vlan-plans` | Planned VLAN definitions | domain/site/status, cursor, limit |
| `GET /api/system/devices/{id}/network-diff` | Planned/observed/imported/manual comparison | as-of revision, domain, resolution state |
| `GET /api/system/import-runs/{id}` | Import progress and bounded errors | — |

Responses include `revision`, `sourceKind`, `sourceRef`, `observedMs`, `validity`, `conflictState`, `effectiveSourceKind` and a stable `asOf`. They never imply that an absent observed row means the planned row is currently configured.

## Write endpoints

- `POST /api/system/devices/{id}/addresses`: add one address with source/provenance and optional primary candidate.
- `PUT /api/system/devices/{id}/addresses/{addressId}`: update an address using `If-Match` or `expectedRevision`.
- `POST /api/system/devices/{id}/network-memberships`: add/verify a bounded domain membership.
- `POST /api/system/vlan-plans`: create a planned VLAN in an explicit domain.
- `PUT /api/system/vlan-plans/{id}`: revise a plan with `If-Match`.
- `POST /api/system/import-runs`: create an idempotent import envelope; rows are submitted with a source fingerprint and dry-run flag.
- `POST /api/system/network-reviews`: record a manual verify/reject/defer decision against exact compared revisions.

No endpoint silently changes an observed row when editing a plan. A write that detects a duplicate, stale revision or scope mismatch returns a structured 409 containing `code`, `resource`, `currentRevision`, `submittedRevision`, `conflicts` and `retryable`.

## Permissions

Reads require device-read permission and are site-scope filtered. Plan/import/manual-review writes require device-admin permission. Any future device-configuration write requires a stronger explicit operation permission and must not be added as a side effect of a planning endpoint. The UI must not rely on hidden buttons as authorization.

## Batch and audit reuse

Address/VLAN planning bulk edits should reuse the P2-A preview/confirm/cancel/retry operation engine rather than introduce a second transaction protocol. The operation payload references immutable target IDs, expected revisions and redacted requested values. Each operation is owner-bound, idempotent, bounded and audited. A conflict returns a previewable resolution state; it never auto-overwrites a newer observed/manual value.

## Import contract

Imports are dry-run by default. A repeated `(sourceRef, contentFingerprint, idempotencyKey)` returns the original run/result. Row-level errors identify scope, normalized key, reason and whether the row was skipped, conflicted or accepted. The import cannot write to Production/Dev Authority without an explicit future release gate.
