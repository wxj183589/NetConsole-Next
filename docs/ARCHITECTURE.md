# NetConsole Next architecture status

Status: `IN_PROGRESS`. This describes code present on the G1 development line; it is not a production deployment claim.

## Current process boundary

```text
NetConsole.Next.Service
  ├─ Windows Service host adapter / generic host lifecycle
  ├─ loopback-only health and task status endpoints
  ├─ UDP Syslog receiver → bounded channel → Raw NDJSON segment writer
  ├─ SQLite catalog and startup Raw reconciliation
  ├─ process, queue, collection and disk-free monitoring
  ├─ bounded, count-retained local rolling logs
  └─ bounded device collection scheduler core (not yet wired to a device driver/API)

NetConsole.Next.LocalManager
  └─ local CLI for Windows service status/start/stop/restart and opening loopback/data directories
```

The Service has no UI dependency. The Web endpoint binds to `127.0.0.1`; remote management authentication and authorization are not implemented. UDP remote binding requires explicit opt-in. The Local Manager is a local process and does not own the Service lifetime.

## Storage boundary

The new data root is independent from the existing NetConsole product. It contains `catalog.db`, `raw/`, a root marker, and (when recovery repairs a corrupt tail) `recovery-evidence/`. `catalog.db` is the metadata authority; Raw bytes remain files. The damaged tail evidence is not served by an API. See [DATABASE_DESIGN.md](DATABASE_DESIGN.md).

## Collection boundary

Syslog is the only connected collector. `BoundedCollectionScheduler` provides finite admission, concurrency, per-request timeout/cancel, and an `IDeviceSession` lifecycle seam for later SSH/SNMP work. Tests use a fake command-echo session. No SSH/SNMP implementation is connected and no device command runs in the Service today.

## State and limits

The service run and Syslog run are written to `task_runs`. Startup marks stale `RUNNING` tasks `INTERRUPTED`. Resource snapshots and scheduler snapshots are in memory; scheduler history is capped at 256 completed requests. SQLite uses WAL and `synchronous=FULL`; the file logger rotates at 5 MiB and retains at most 14 service log files. The Windows installer assigns a dedicated virtual service account with read/execute access to the publish directory and modify access to the isolated data root. There is not yet a versioned migration runner, managed backup/restore, explicit checkpoint policy, authenticated Web UI, or supported-target Windows service installation evidence.
