# NetConsole Next architecture status

Status: `IN_PROGRESS`. This describes code present on the G1 development line; it is not a production deployment claim.

## Current process boundary

```text
NetConsole.Next.Service
  ├─ Windows Service host adapter / generic host lifecycle
  ├─ loopback-only health and task status endpoints
  ├─ UDP Syslog receiver → bounded channel → Raw NDJSON segment writer
  ├─ SQLite v2 catalog, incremental startup reconciliation and low-priority integrity audit
  ├─ process, queue, collection and disk-free monitoring
  ├─ bounded, count-retained local rolling logs
  └─ bounded scheduler backed by task definitions/runs (simulation only; no device driver/API)

NetConsole.Next.LocalManager
  └─ local CLI for Windows service status/start/stop/restart and opening loopback/data directories
```

The Service has no UI dependency. The Web endpoint binds to `127.0.0.1`; remote management authentication and authorization are not implemented. UDP remote binding requires explicit opt-in. The Local Manager is a local process and does not own the Service lifetime.

## Storage boundary

The new data root is independent from the existing NetConsole product. It contains `catalog.db`, `raw/`, a root marker, a recovery-in-progress marker during reconciliation, and (when recovery repairs a corrupt tail) `recovery-evidence/`. `catalog.db` is the metadata authority; Raw bytes remain files. The damaged tail evidence is not served by an API. See [DATABASE_DESIGN.md](DATABASE_DESIGN.md).

Startup still walks the Raw directory and checks indexed file metadata. It skips file contents and catalog writes only when a finalized row has a checksum and its size/last-write timestamp match the checkpoint captured after full verification. Older rows without checkpoints are read and hashed once. A low-priority hosted maintenance task starts after ten minutes and fully audits at most one finalized file per minute. This metadata shortcut assumes ordinary filesystem timestamp behavior; it does not detect a same-size rewrite that also preserves the timestamp until that full audit reaches the file.

Raw recovery distinguishes a historical segment issue from a critical SQLite, Raw-root, or Raw-write failure. Health reports `HEALTHY`, `DEGRADED`, or `FAULTED`. Critical storage failures keep the loopback health endpoint available when the host can start, but collection admission is disabled. A runtime Raw/SQLite write failure stops UDP admission, retains `.open` or finalized evidence where possible, and reports `FAULTED`.

## Collection boundary

Syslog is the only connected collector. `BoundedCollectionScheduler` provides finite admission, concurrency, per-request timeout/cancel, and an `IDeviceSession` lifecycle seam for later SSH/SNMP work. Tests use a fake command-echo session. No SSH/SNMP implementation is connected and no device command runs in the Service today.

## Persistent task model and limits

Service and Syslog lifecycle rows, plus simulated device `Task Definition`/`Task Run` rows, are persisted in SQLite. Runs use a bounded queue and record queue/start/deadline/end times, state, definition and failure reason. On startup, queued/connecting/running runs become `INTERRUPTED`; `NEVER` is the only restart policy and requires an explicit retry. The scheduler has no task submission API and does not execute a real device driver.

Resource snapshots include CPU, working set, thread/Windows handle count, and data-volume free space. UDP health includes receive/admit/drop/flush counts, queue high-watermark and bounded-bucket Raw flush latency estimates. SQLite uses WAL, a 5-second busy timeout and `synchronous=FULL`; the file logger rotates at 5 MiB and retains at most 14 service log files. The Windows installer assigns a dedicated virtual service account with read/execute access to the publish directory and modify access to the isolated data root. Schema upgrades are additive through v2; managed backup/restore, explicit checkpoint policy and authenticated Web UI are not implemented. Actual Windows SCM CI evidence is pending for this branch.
