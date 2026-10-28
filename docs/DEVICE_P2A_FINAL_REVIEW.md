# Device Management P2-A Final Hardening Review

## Review boundary

This review covers the P2-A batch operation service, persisted recovery behavior, browser controls, and the final PR22 change set. PR20/PR21/PR22 remain unmerged Draft PRs. The review does not authorize production-data migration, real device access, or Schema v13 implementation.

## Findings and fixes

| Severity | Finding / trigger | Impact | Fix and regression evidence |
|---|---|---|---|
| P1 | Startup recovery previously used a zero progress total and coupled the recovery audit insert to the state update transaction. An audit failure could roll back recovery and leave the operation unfinished. | Recovery state could be misleading or remain `RUNNING` after restart. | Recovery now reads the persisted item count, commits `INTERRUPTED` first, and writes recovery audit best-effort. Real-process B/C crash scenarios and C# tests pass. |
| P1 | Conflict/failure finalization previously coupled terminal state/item updates to the audit insert. | An audit/storage fault could strand a failed or conflicted operation in `RUNNING`. | Terminal state commits first; audit errors are logged separately. E_FAILURE_AUDIT injects both device and audit faults, restarts the service, and verifies `FAILED`, unchanged devices, no replay and no success audit. |
| P1 | The browser did not make retry intent explicit and the input-dialog title could retain the previous batch kind after a preview was closed. | Operators could misread a retry as a fresh submit or configure the wrong operation label. | Added explicit `安全重试` labeling and separate input/preview kind computed labels. The real Chrome flow covers all five kinds and retry. |
| P0 | No confirmed P0 finding in the P2-A scope. | — | No P0 change was required. Security and ownership regression tests remain green. |

## Security and authorization checks

The existing boundary tests remain part of the gate: anonymous access, admin read, admin write denial, superadmin write, CSRF protection, cross-user owner denial, idempotency ownership, explicit target persistence, and audit redaction. The browser run additionally verifies that `admin` does not receive batch-write controls and receives the expected 403 for a direct write attempt.

## Data and transaction checks

Schema v12 remains the implemented boundary. Batch operation/device-item tables retain their existing foreign-key and retention design, including deliberate non-FK explicit device targets so missing devices can produce `DEVICE_NOT_FOUND`. The crash harness verifies WAL, SQLite integrity, foreign-key checks, rollback before commit, commit durability after restart, and no automatic replay.

## Remaining review items

- PR21 and PR22 still need an authorized formal reviewer approval; no approval is inferred from CI or this review.
- P2B is delivered separately as a design-only branch/PR. It contains no Schema v13 migration or production API implementation.
- Existing NU1903 and Vite large-chunk warnings remain non-blocking follow-up items.
- GUI installation and real-device acceptance remain separate gates and are not claimed here.

## Evidence pointers

- Crash/restart procedure: `docs/DEVICE_BATCH_CRASH_RECOVERY.md`.
- Browser diagnostics and screenshots: `docs/DEVICE_P2A_BROWSER_DIAGNOSTICS.md` and `artifacts/device-p2a-browser/`.
- Existing operation state and concurrency contracts: `docs/DEVICE_BATCH_OPERATION_STATE_MACHINE.md`, `docs/DEVICE_BATCH_CONCURRENCY.md`, and `docs/DEVICE_CSV_CONCURRENCY_AND_LIMITS.md`.
