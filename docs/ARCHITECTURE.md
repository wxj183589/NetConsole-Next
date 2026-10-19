# NetConsole Next architecture status

Status: `IN_PROGRESS`. This describes code present on the G1 development line; it is not a production deployment claim.

## Current process boundary

```text
NetConsole.Next.Service
  ├─ Windows Service host adapter / generic host lifecycle
  ├─ loopback-only health and task status endpoints
  ├─ UDP Syslog receiver → packet/byte-bounded channel → batched Raw NDJSON segment writer
  ├─ SQLite v3 catalog, incremental startup reconciliation, integrity audit and explicit issue disposition
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

Raw recovery distinguishes a historical segment issue from a critical SQLite, Raw-root, or Raw-write failure. Health reports `HEALTHY`, `DEGRADED`, or `FAULTED`; degradation causes are tracked independently so resolving Raw integrity cannot clear interrupted-task, interrupted-recovery, or maintenance-runtime warnings. An interrupted recovery keeps that process `DEGRADED` after replay completes; a clean subsequent process start clears this transient cause. Unresolved integrity audits and task runs interrupted during the previous process lifetime keep health at `DEGRADED`. A later full integrity check can record `RESOLVED_VERIFIED`, including a later successful retry of a transient `MAINTENANCE_FAILED` audit. An operator may record `QUARANTINED` for unchanged, hash-verified bytes; this is a logical Catalog disposition only. It does not move or delete Raw bytes or recovery evidence. If bytes change later, a new audit is recorded and health degrades again. Critical storage failures keep the loopback health endpoint available when the host can start, but collection admission is disabled. A runtime Raw/SQLite write failure stops UDP admission, retains `.open` or finalized evidence where possible, and reports `FAULTED`.

## Collection boundary

Syslog is the only connected collector. It requests a 4 MiB UDP receive buffer and records the OS-reported effective value. One `ReceiveAsync` reader feeds a channel bounded by 8,192 packets and 32 MiB of payload. The Raw writer stages NDJSON without an intermediate Base64 string and starts a sync batch when it reaches 256 records, 256 KiB, 100 ms age, or a 4 MiB segment boundary. The 100 ms value schedules a flush; slow storage can make completion take longer. Each completed batch still calls `Flush(true)`; SQLite retains `synchronous=FULL`. Normal stop drains and seals the current segment. This does not guarantee zero loss on process kill or power failure: packets still in the kernel, queued packets and staged records not yet synchronized may be lost; an `.open` prefix that reached storage is preserved for recovery. The packet count limit was raised from 2,048 after measurements showed segment-seal stalls up to about 1.2 seconds while the separate 32 MiB payload bound was mostly unused. A paired 100,000-datagram test supports this larger bounded window on the cloud runner; it is not a sustainable industrial-host throughput claim.

`BoundedCollectionScheduler` provides finite admission, concurrency, per-request timeout/cancel, and an `IDeviceSession` lifecycle seam for later SSH/SNMP work. Tests use a fake command-echo session. No SSH/SNMP implementation is connected and no device command runs in the Service today.

## Persistent task model and limits

Service and Syslog lifecycle rows, plus simulated device `Task Definition`/`Task Run` rows, are persisted in SQLite. Runs use a bounded queue and record queue/start/deadline/end times, state, definition and failure reason. On startup, queued/connecting/running runs become `INTERRUPTED`; `NEVER` is the only restart policy and requires an explicit retry. The scheduler has no task submission API and does not execute a real device driver.

Resource snapshots include CPU, working set, thread/Windows handle count, and data-volume free space. UDP health includes received/admitted/application-drop/flushed counts, queue packet/byte high-watermarks, requested/effective socket buffer values, and rolling Raw sync, segment-seal and SQLite-registration latency windows. SQLite uses WAL (enabled once on initialization), a 5-second busy timeout and `synchronous=FULL`; the file logger rotates at 5 MiB and retains at most 14 service log files. The Windows installer assigns a dedicated virtual service account with read/execute access to the publish directory and modify access to the isolated data root. GitHub Windows Server 2025 CI has exercised actual SCM installation, control, crash recovery and uninstall retention. Schema v3 adds append-only integrity disposition state; managed backup/restore and an explicit WAL checkpoint policy are not implemented. Target Windows versions and field environments remain pending.
