# Database and Raw layout

Status: schema v5 is implemented with additive idempotent upgrades from v1-v4. Linux Release build, repeated migration, SQLite integrity, Raw reconciliation, atomic task/result rollback and synthetic H3C output recovery tests passed locally. Current G2.1 Windows CI is pending; PR #10 Server 2025 CI is historical baseline, not evidence for the v5/DPAPI changes. Target OS and live-device compatibility remain pending. Backup/restore remains `NOT_STARTED`.

## Current isolated data root

```text
<data-root>/
├── .netconsole-next-root
├── .raw-recovery-in-progress # present during reconciliation; removed after a noncritical pass completes
├── local-api-token            # random bearer token; Unix mode 0600, Windows inherits restricted data-root ACL
├── catalog.db                 # SQLite authority for sources, Raw manifest and task runs
├── catalog.db-wal / -shm      # SQLite-managed WAL files while open
├── raw/syslog/YYYY/MM/DD/     # Raw NDJSON segments (.open while writing, .ndjson after seal)
├── raw/h3c/<profile-id>/YYYY/MM/DD/ # one sealed command response per H3C task run
├── credentials/h3c-credentials.v1.json # Windows DPAPI LocalMachine ciphertext keyed by reference
├── logs/                      # rolling service diagnostics (5 MiB segments, 14-file cap)
├── published/ inbox/ archive/ backup/ # prepared isolated directories; optional services are not implemented
└── recovery-evidence/         # verified corrupt-tail bytes and JSON metadata
```

The Windows default root is `%ProgramData%/NetConsoleNext/data`; elsewhere it must be set explicitly with `NETCONSOLE_NEXT_DATA_ROOT`. The Service rejects filesystem roots, symlink/reparse roots, unknown root markers, and nonempty unmarked directories. The old NetConsole data root is not a valid input.

## Schema versions

`schema_versions` currently records v1 through v5. Startup applies the original v1 seed idempotently, adds v2 checkpoints/audits/tasks, creates v3 disposition state, adds v4 H3C identity/result tables, and applies v5 Raw/task recovery fields and status validation in idempotent transactions. Reopening an already upgraded database is tested.

### Schema v1 base

| Table | Current purpose |
| --- | --- |
| `schema_versions` | Applied schema versions. |
| `sites` | Local site seed for the prototype. |
| `collection_sources` | Collection source identity and kind. |
| `raw_segments` | Relative path, state, time range, record/byte count, SHA-256, and seal time. |
| `task_runs` | Service and collector run lifecycle, including stale-run recovery. |
| `audit_events` | Reserved schema for future audited operations; no management mutations use it yet. |

### Schema v2 additions

| Table / columns | Purpose |
| --- | --- |
| `raw_segments.checkpoint_size`, `checkpoint_write_ticks`, `last_verified_ms`, `last_full_audit_ms` | Skip opening a finalized Raw file at startup when its catalog SHA, file size and last-write timestamp checkpoint are present and unchanged. |
| `raw_integrity_audits` | Append expected/observed checksum and byte evidence, reason, evidence path, and detail when recovery or maintenance detects a mismatch. |
| `task_definitions` | Minimal simulated task type, target, command request, timeout, enabled flag and restart policy. |
| `task_runs.definition_id`, `queued_ms`, `deadline_ms`, `failure_reason` | Persist bounded scheduler lifecycle and interruption reason alongside existing timestamps/state. |

### Schema v3 additions

| Table | Purpose |
| --- | --- |
| `raw_issue_dispositions` | One append-only disposition per integrity audit. `QUARANTINED` records actor, reason, evidence reference, hash and time while retaining the original Raw bytes. `RESOLVED_VERIFIED` records a successful later full audit. Changed bytes receive a new audit; dispositions do not overwrite or erase prior evidence. |

### Schema v4 additions

| Table | Purpose |
| --- | --- |
| `h3c_device_profiles` | H3C host/port/username, enabled state, a credential **reference**, and normalized SHA-256 SSH host-key pin. Passwords are not stored. Connection identity is immutable for a profile ID; host, credential-reference or host-key changes require a new ID. Windows uses the LocalMachine DPAPI vault; Linux/Kylin has no production provider. The environment-variable provider is available only in explicit Development mode. |
| `task_run_raw_outputs` | One command/output manifest per H3C task run, referencing the sealed Raw segment and storing command, decoded output byte count/SHA-256, segment SHA-256, parsed JSON, capture time, `capture_status` (`COMPLETE/PARTIAL`) and `parse_status` (`PARSED/EMPTY/UNRECOGNIZED/PARTIAL/UNCLASSIFIED`). |

### Schema v5 additions

| Table / columns | Purpose |
| --- | --- |
| `raw_segments.task_run_id` | Links a single-record H3C Raw segment to its persisted Task Run so recovery can rebuild a missing result manifest after a process crash. The index is partial and only covers non-null task IDs. |
| `task_run_raw_outputs.capture_status`, `parse_status` | Separates output completeness from parser recognition. SQLite triggers restrict both values to their defined states. |
| Atomic completion transaction | `CompleteTaskRunWithRawOutput` inserts/validates the result manifest and transitions the run terminal state in the same SQLite transaction. An injected terminal update failure rolls back the manifest insert. Startup recovery may restore the manifest from verified Raw but leaves task state `INTERRUPTED`; it never changes a crashed task to success. |

Initialization enables WAL once and verifies the resulting mode. Each later SQLite connection sets `synchronous=FULL`, foreign keys, and a 5-second busy timeout without reissuing the journal-mode pragma, avoiding needless mode checks during concurrent writes. A write-lock test confirms busy errors return instead of hanging. Startup runs `quick_check(1)` and `foreign_key_check` before enabling collection. Catalog unavailable or critical recovery state prevents new collection. Writes are short-lived and segment registration happens only after the file is sealed.

## Raw write and recovery

Syslog payload bytes are Base64 encoded in newline-terminated JSON records. H3C command output is the **SSH.NET-decoded UTF-8 output**, not SSH wire bytes. It is Base64 encoded in a typed NDJSON record with source/profile/run IDs, exact allowlisted command, host, output length/hash and `capture_status`. Combined stdout/stderr is drained concurrently and limited to 256 KiB; only decoded stdout is written to Raw, never stderr or diagnostic logs. Output limit, paging response, nonzero command result or a disconnect after output retains the available stdout prefix as `PARTIAL`. H3C records are one command per segment. Recovery verifies embedded output hashes, status, task-run ID, segment hash and source/profile folder. If a Raw-sealed Task Run has no result row, it reconstructs the result row idempotently while retaining the existing task terminal/interrupted status. Before truncating a malformed or partial tail, recovery writes and verifies the exact tail bytes in `recovery-evidence/`; failed evidence writes leave source bytes unchanged.

The local API bearer token is intentionally outside SQLite and lives in `local-api-token`. Windows H3C secrets are also outside SQLite: only DPAPI ciphertext is persisted in `credentials/h3c-credentials.v1.json`, keyed by reference; the service identity relies on the restricted inherited ProgramData ACL. Linux/Kylin has no DPAPI implementation. Explicit Development environment credentials are not a production provider.

Every startup enumerates `.open` and `.ndjson` paths to discover unindexed files and compare indexed state. A `FINALIZED` row is trusted for startup only when the expected SHA exists and file size plus last-write UTC ticks equal its post-verification checkpoint. In that case startup performs filesystem metadata checks but reads zero file contents and writes no catalog row. v1 rows have no checkpoint, so the first v2 startup fully validates them. A size/time change, `INCOMPLETE`/`MISSING` state, `.open`, orphan, or path error forces full inspection; a checksum mismatch is appended to `raw_integrity_audits` and the Raw file remains on disk marked `INCOMPLETE`.

The service writes `.raw-recovery-in-progress` with write-through before reconciliation. A process killed during recovery leaves the marker; the next startup replays the scan and reports `RECOVERY_INTERRUPTED`. On successful noncritical completion it clears the marker. A critical SQLite/raw-root error leaves it for another attempt. Persisted `INCOMPLETE`/`MISSING` segments and task runs changed to `INTERRUPTED` also keep the health state degraded on later starts. A low-priority audit begins after ten minutes, then fully inspects at most one finalized segment per minute, including a segment whose same-size rewrite preserved its last-write timestamp.

An `.open` segment becomes a visible `.ndjson` recovery segment with state `INCOMPLETE`, even if its current bytes are parseable, because clean sealing was not recorded. Valid unindexed `.ndjson` is reconciled; missing catalog files become `MISSING`. Incomplete segments are never upgraded to `FINALIZED` by recovery.

There is no automatic evidence retention/cleanup, backup workflow or periodic WAL checkpoint policy. Disk-full behavior is tested with an injected stream that writes a real partial `.open` file before throwing; actual device/filesystem exhaustion and power loss remain pending real-environment tests. The service reports/faults but does not promise zero data loss. `QUARANTINED` is a logical Catalog disposition, not a physical move or access-control isolation.
