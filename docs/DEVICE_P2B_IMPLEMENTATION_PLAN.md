# Device Management P2-B Executable Implementation Plan

## Branch and review rule

The design branch is based on `codex-A/next-device-management-p2a-20261026` at P2-A head `c738376218b17ceb2ea041b2d31033c4f41dcf84`. This branch contains documentation only. Each implementation PR must branch from the immediately preceding accepted PR, remain independently reviewable, and keep P2-A behavior green.

## Proposed PR breakdown

| PR | Base / head | Scope | Required evidence | Main risk |
|---|---|---|---|---|
| P2B-1 contracts | P2-A head → `codex-A/next-device-management-p2b-contracts` | freeze terminology, source/authority enums, conflict payloads, feature gate, API fixtures | contract tests, compatibility review, no DB change | ambiguous authority semantics |
| P2B-2 schema | P2B-1 → `codex-A/next-device-management-p2b-schema-v13` | implement reviewed v13 migration, indexes, backfill and v12 projection | migration/restart/rollback fixture, WAL/FK/integrity checks | incorrect scope uniqueness or partial migration |
| P2B-3 read boundary | P2B-2 → `codex-A/next-device-management-p2b-read-api` | repositories/services and bounded address/domain/VLAN/diff reads | API auth/scope/pagination/fixture tests | leaking cross-site or stale data |
| P2B-4 write/import boundary | P2B-3 → `codex-A/next-device-management-p2b-write-api` | optimistic writes, import dry-run, review records, P2-A operation reuse | conflict/idempotency/audit/redaction tests | silent overwrite or duplicate imports |
| P2B-5 UI and acceptance | P2B-4 → `codex-A/next-device-management-p2b-ui` | detail tabs, comparison, import preview, responsive/light-dark UI | typecheck, unit tests, browser diagnostics/screenshots, CI | confusing planned-v-observed state |

## Sequencing and gates

1. Approve this design and the P2B-1 vocabulary before any migration code.
2. Run a schema preflight against an isolated v12 fixture; never use Production/Dev Authority.
3. Require a reviewed migration/rollback plan before P2B-2 can be marked ready.
4. Keep v13 writes feature-gated until read API and conflict semantics are accepted.
5. Require API boundary and audit review before UI work depends on the new endpoints.
6. Run the P2-A crash, idempotency and authorization regression suite at every implementation PR.
7. Real device collection and real VLAN changes are separate release gates and cannot be inferred from fixture/CI success.

## Definition of done for P2-B implementation

The implementation is complete only when all six design docs are reflected in code/tests, v12 compatibility is proven, migration restart/rollback is proven, source authority and conflicts are visible, no cross-scope identity guesses exist, the browser diagnostics are clean, CI passes on Ubuntu and Windows, and an authorized reviewer approves the PR chain. This design branch intentionally stops before that point.
