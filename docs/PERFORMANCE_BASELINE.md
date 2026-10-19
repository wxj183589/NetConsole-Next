# Performance baseline

Status: `NOT_STARTED` for quantitative load characterization. No value below is inferred from code shape.

| Target | Hardware | Idle CPU / memory | Sustained Syslog | Recovery / I/O | Status |
| --- | --- | --- | --- | --- | --- |
| Baseline A | 2 cores / 4 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |
| Baseline B | 4 cores / 4 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |
| Baseline C | 4 cores / 8 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |

Initial research goals are idle working set at or below 150 MiB and idle CPU at or below 2%; these are targets, not results. Current health API exposes process CPU, working set, thread count, Windows handle count, data-volume free bytes, UDP counters, queue depth, and last receive/persist timestamps. No repeatable load generator or measurements for receive rate, p99 persistence latency, disk growth, or SQLite throughput are checked in yet.

Next baseline should run a deterministic local UDP load against an isolated temporary data root, record test machine/runtime settings and actual received/accepted/flushed/dropped counts, and compare idle, sustained, recovery, and disk-pressure cases. Continuous 72-hour and 7-day industrial-host runs remain separate gates.
