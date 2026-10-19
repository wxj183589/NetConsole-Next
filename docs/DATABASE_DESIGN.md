# Database and Raw layout

Status: schema v2 is implemented with an additive idempotent upgrade from v1. Linux build, startup, repeated migration, SQLite integrity and Raw reconciliation tests passed locally. Windows Service CI is pending for the current branch. Backup/restore remains `NOT_STARTED`.

## Current isolated data root

```text
<data-root>/
├── .netconsole-next-root
├── .raw-recovery-in-progress # present during reconciliation; removed after a noncritical pass completes
├── catalog.db                 # SQLite authority for sources, Raw manifest and task runs
├── catalog.db-wal / -shm      # SQLite-managed WAL files while open
├── raw/syslog/YYYY/MM/DD/     # Raw NDJSON segments (.open while writing, .ndjson after seal)
├── logs/                      # rolling service diagnostics (5 MiB segments, 14-file cap)
├── published/ inbox/ archive/ backup/ # prepared isolated directories; optional services are not implemented
└── recovery-evidence/         # verified corrupt-tail bytes and JSON metadata
```

The Windows default root is `%ProgramData%/NetConsoleNext/data`; elsewhere it must be set explicitly with `NETCONSOLE_NEXT_DATA_ROOT`. The Service rejects filesystem roots, symlink/reparse roots, unknown root markers, and nonempty unmarked directories. The old NetConsole data root is not a valid input.

## Schema versions

`schema_versions` currently records v1 and v2. Startup applies the original v1 seed idempotently, adds missing v2 columns in a transaction, applies v2 tables/indexes, and records version 2. Reopening an already upgraded database is tested.

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

SQLite connections enable WAL, `synchronous=FULL`, foreign keys, and a 5-second busy timeout before requesting journal mode. A write-lock test confirms busy errors return instead of hanging. Startup runs `quick_check(1)` and `foreign_key_check` before enabling collection. Catalog unavailable or critical recovery state prevents new collection. Writes are short-lived and segment registration happens only after the file is sealed.

## Raw write and recovery

Syslog payload bytes are Base64 encoded in newline-terminated JSON records. Recovery accepts only lines that parse and satisfy the record shape; it hashes the valid prefix when a file needs full inspection. Before truncating the first malformed or partial tail, it writes the exact tail bytes into `recovery-evidence/*.tail.bin`, flushes and hashes them, then writes metadata with source-relative path, byte offset, length, and SHA-256. If evidence cannot be written and verified, the original Raw file stays unchanged. Re-running recovery uses a deterministic evidence identity and does not create duplicate catalog rows.

Every startup enumerates `.open` and `.ndjson` paths to discover unindexed files and compare indexed state. A `FINALIZED` row is trusted for startup only when the expected SHA exists and file size plus last-write UTC ticks equal its post-verification checkpoint. In that case startup performs filesystem metadata checks but reads zero file contents and writes no catalog row. v1 rows have no checkpoint, so the first v2 startup fully validates them. A size/time change, `INCOMPLETE`/`MISSING` state, `.open`, orphan, or path error forces full inspection; a checksum mismatch is appended to `raw_integrity_audits` and the Raw file remains on disk marked `INCOMPLETE`.

The service writes `.raw-recovery-in-progress` with write-through before reconciliation. A process killed during recovery leaves the marker; the next startup replays the scan and reports `RECOVERY_INTERRUPTED`. On successful noncritical completion it clears the marker. A critical SQLite/raw-root error leaves it for another attempt. A low-priority audit begins after ten minutes, then fully inspects at most one finalized segment per minute, including a segment whose same-size rewrite preserved its last-write timestamp.

An `.open` segment becomes a visible `.ndjson` recovery segment with state `INCOMPLETE`, even if its current bytes are parseable, because clean sealing was not recorded. Valid unindexed `.ndjson` is reconciled; missing catalog files become `MISSING`. Incomplete segments are never upgraded to `FINALIZED` by recovery.

There is no automatic evidence retention/cleanup, backup workflow or periodic WAL checkpoint policy. Disk-full behavior is tested with an injected stream that writes a real partial `.open` file before throwing; actual device/filesystem exhaustion and power loss remain pending real-environment tests. The service reports/faults but does not promise zero data loss.
