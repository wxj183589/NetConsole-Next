# Device Batch Crash/Restart Recovery Evidence

## Scope

This document records the P2-A hardening evidence for the real .NET Service path. The harness starts the published `NetConsole.Next.Service` process on loopback with a fresh temporary data root, pauses at synchronization points implemented in production code, kills only that child process, restarts the same service against the same isolated root, and verifies the persisted result through the API and SQLite.

It does not use `D:\NetConsoleData`, `D:\NetConsoleData-dev`, real devices, SSH/Telnet/COM, or VLAN configuration.

## Guarded crash hooks

`src/NetConsole.Next.Service/DeviceBatchCrashTestHooks.cs` is inert unless all of the following are true:

- `NETCONSOLE_NEXT_BATCH_CRASH_TEST=1`;
- the service environment is `Development`;
- a hook directory and explicit stage are configured.

The production/API path therefore has no pause or crash behavior by default. The parent harness verifies the marker PID before killing the process.

## Command

Run after a Release build from the P2-A worktree:

```text
python tests/device_batch_crash_recovery/device_batch_crash_recovery.py --repo D:\NetConsoleNext-P2A --evidence D:\study\NetConsole-Workspace\diagnostic\netconsole-next-p2a-crash-evidence.json
```

The evidence file is temporary diagnostic output and is not a data Authority. The harness removes each temporary data root after the scenario finishes.

## Scenarios and expected result

| Scenario | Kill point | Restart expectation | Result |
|---|---|---|---|
| A_BEFORE_CONFIRM | after preview, before confirm | `PENDING_CONFIRM`; no device mutation; explicit cancel remains possible | PASS |
| B_AFTER_CLAIM | after operation becomes `RUNNING`, before updates | `INTERRUPTED`; no auto replay; no success audit | PASS |
| C_BEFORE_COMMIT | after first device update, before transaction commit | `INTERRUPTED`; all device updates roll back | PASS |
| D_AFTER_COMMIT | after transaction commit | `SUCCEEDED`; repeated confirm is idempotent and snapshots are unchanged | PASS |
| E_FAILURE_AUDIT | after injected device failure and injected failure-audit error | `FAILED`; no device mutation, no replay, and the audit failure cannot strand `RUNNING` | PASS |

Every scenario also checks SQLite `journal_mode=wal`, `integrity_check=ok`, an empty `foreign_key_check`, and unchanged profile/source fixture counts. The E scenario is deliberately an isolated trigger-injected fault and does not alter product data.

## Code hardening covered

- Recovery first commits the terminal `INTERRUPTED` state and actual item count, then attempts the recovery audit separately.
- Conflict/failure finalization first commits terminal operation/item state, then performs best-effort audit persistence; audit failure is logged rather than leaving the operation `RUNNING`.
- The existing transactional device updates remain serializable and all-or-nothing.
- The retry path continues to require a persisted failed operation and reuses the existing idempotency/ownership checks.

The detailed machine-readable run is regenerated after the final P2-A commit and its `gitSha` is recorded in the final acceptance report.
