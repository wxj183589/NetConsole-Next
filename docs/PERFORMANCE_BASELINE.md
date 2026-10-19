# Performance baseline

Status: a repeatable Linux baseline exists. The figures below are a cloud-container measurement, not an industrial-host acceptance result.

| Target | Hardware | Idle CPU / memory | Sustained Syslog | Recovery / I/O | Status |
| --- | --- | --- | --- | --- | --- |
| Baseline A | 2 cores / 4 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |
| Baseline B | 4 cores / 4 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |
| Baseline C | 4 cores / 8 GB | Not measured | Not measured | Not measured | `PENDING_REAL_ENVIRONMENT` |

Initial research goals are idle working set at or below 150 MiB and idle CPU at or below 2%; these remain targets. `tests/perf_baseline.py` records environment, CPU, working set, thread/fd count, process I/O syscall/byte counters, UDP counters, queue high-watermark, Raw flush latency, SQLite per-commit latency, startup recovery time, and data-directory growth. The service exposes bounded-bucket Raw flush p50/p95 estimates and a maximum; measurements above the largest histogram bucket are capped/unknown. Physical block-device IOPS are unavailable inside this container and are reported as `null`.

## Cloud measurement (2026-10-19)

Command: `python3 tests/perf_baseline.py --packets 100000 --queue-burst 50000 --history-files 1000 --sqlite-writes 500 --idle-seconds 6 --rate 20000`

Environment: Linux container, .NET 10, Python 3.12.14, 3 visible processors, isolated temporary data root. UDP “received” is what the application observed; sender successes do not prove network delivery, and the gap is not attributed to a particular kernel/network layer.

| Scenario | Measured result |
| --- | --- |
| Idle, 6 seconds | Health CPU sample 4.92%; working set 93,831,168 bytes; 19 threads; 230 process file descriptors. This includes a single short sample and is not a long-run idle average. |
| UDP, 100,000 attempted at 20,000/s | 95,145 received, 95,145 queued, 0 application drops, 95,145 flushed; 0 queue depth at end; high watermark 708/2,048. 41,678,352 bytes of data-root growth. |
| Raw flush latency during UDP | 18,091 samples; histogram p50/p95 upper bound 1 ms; max 748.626 ms. An earlier run of the same profile measured max 30.275 ms, so the shared-host maximum is variable. |
| Process under UDP load | 45.06% normalized CPU; working set 107,704,320 bytes; 25 threads; 231 file descriptors; 9,610 read syscalls and 18,534 write syscalls. Kernel physical byte counters reported 0 in this container. |
| Immediate queue burst, 50,000 attempted | 9,537 received/accepted/flushed, 0 application drops, high watermark 1,756/2,048. 40,463 sends were not observed by the application; no attribution is made. |
| SQLite, 500 single-row commits | p50 0.006760 ms, p95 0.013685 ms, max 2.523610 ms, mean 0.018214 ms. This is a local SQLite probe table on the container filesystem. |
| Startup with 1,000 trusted 11,016-byte Raw files | 1,011 files scanned/skipped, 0 content bytes read, 0 bytes hashed, Catalog recovery 271 ms, process-start-to-health 1,948 ms. Data-root growth 34,962 bytes. |
| Final data root | 59,517,979 bytes after UDP load, recovery fixtures and SQLite probe writes. |

This run does not establish performance on 2-core/4-GB or 4-core/8-GB hardware, physical IOPS, kernel UDP loss, 72-hour stability, real disk exhaustion, or Windows behavior. The burst did not fill the UDP ingress queue; the scheduler unit test separately filled its bounded device queue and verified rejection. Flush maximum varied from 30 ms to 749 ms across runs on the shared host, which is why these measurements do not support a low-configuration performance claim.

Next baseline should run on the specified low-configuration industrial host and add 72-hour/7-day runs plus platform-supported block-device IOPS. Continuous field runs remain separate gates.
