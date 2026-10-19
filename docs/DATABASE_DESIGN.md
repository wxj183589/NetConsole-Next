# Database and Raw layout

Status: schema v1 is `CI_VERIFIED` for initialization, SQLite integrity checks, and Raw reconciliation. Backup/restore and schema upgrades beyond v1 are `NOT_STARTED`.

## Current isolated data root

```text
<data-root>/
├── .netconsole-next-root
├── catalog.db                 # SQLite authority for sources, Raw manifest and task runs
├── catalog.db-wal / -shm      # SQLite-managed WAL files while open
├── raw/syslog/YYYY/MM/DD/     # Raw NDJSON segments (.open while writing, .ndjson after seal)
└── recovery-evidence/         # verified corrupt-tail bytes and JSON metadata
```

The Windows default root is `%ProgramData%/NetConsoleNext/data`; elsewhere it must be set explicitly with `NETCONSOLE_NEXT_DATA_ROOT`. The Service rejects filesystem roots, symlink/reparse roots, unknown root markers, and nonempty unmarked directories. The old NetConsole data root is not a valid input.

## Schema v1

| Table | Current purpose |
| --- | --- |
| `schema_versions` | Records the initialized schema version (currently v1); no upgrade runner yet. |
| `sites` | Local site seed for the prototype. |
| `collection_sources` | Collection source identity and kind. |
| `raw_segments` | Relative path, state, time range, record/byte count, SHA-256, and seal time. |
| `task_runs` | Service and collector run lifecycle, including stale-run recovery. |
| `audit_events` | Reserved schema for future audited operations; no management mutations use it yet. |

SQLite connections enable WAL, `synchronous=FULL`, foreign keys, and a 5-second busy timeout. Schema SQL is embedded in the Service and applied idempotently at startup. Startup runs `quick_check(1)` and `foreign_key_check` before enabling collection. Writes are short-lived and segment registration happens only after the file is sealed.

## Raw write and recovery

Syslog payload bytes are Base64 encoded in newline-terminated JSON records. Recovery accepts only lines that parse and satisfy the record shape; it re-hashes the valid prefix. Before truncating the first malformed or partial tail, it writes the exact tail bytes into `recovery-evidence/*.tail.bin`, flushes and hashes them, then writes metadata with source-relative path, byte offset, length, and SHA-256. If evidence cannot be written and verified, the original Raw file stays unchanged. Re-running recovery uses a deterministic evidence identity and does not create duplicate catalog rows.

An `.open` segment becomes a visible `.ndjson` recovery segment with state `INCOMPLETE`, even if its current bytes are parseable, because clean sealing was not recorded. Valid unindexed `.ndjson` is reconciled; missing catalog files become `MISSING`. Incomplete segments are never upgraded to `FINALIZED` by recovery.

There is no automatic evidence retention/cleanup, backup workflow, periodic WAL checkpoint policy, or hard-disk/disk-full recovery acceptance yet. A full disk or power loss can still prevent writes; the service reports/faults but does not promise zero data loss.
