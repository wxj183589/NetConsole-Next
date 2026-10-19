# Performance baseline

Status: reproducible Linux container measurements are available for the G1.5 Syslog path. They describe this cloud runner, not an industrial Mini host or production deployment. A larger PPS parameter is not evidence of achieved throughput: all tables below report the sender's measured rate. The G2 H3C work has only synthetic correctness tests; no SSH/device throughput, CPU, memory or session-lifetime measurement is available.

## Environment and accounting

Measurements ran in an isolated Linux container on kernel `6.18.55`, .NET `10.0.402`, Python `3.12.14`, three visible CPUs and a 2.0-CPU cgroup quota. The trace sender and Service ran pinned to distinct CPUs from the allowed affinity set. The temporary data root was isolated per case. Host physical-device IOPS are not exposed by the container overlay; `null` means unavailable, and `/proc` physical byte counters of zero do not mean that no writes occurred.

Trace packets carry a unique sequence, send timestamp, exact payload length and SHA-256 checksum. `sendto` success means only that the sender's local socket accepted a datagram. The Service counters distinguish socket-delivered/application-received, application-enqueued, application-dropped, and Raw-flushed records. `/proc/net/snmp` UDP counters are host-global, not socket-scoped. A gap is attributed to kernel receive-buffer errors only when the counter delta matches it during an otherwise isolated case; unrelated host UDP traffic cannot be ruled out absolutely. Queue drops are measured inside the application.

## Paired queue-capacity measurement

Command for both runs: `tests/perf_baseline.py --packets 100000 --payload-bytes 512 --rate 5000 --queue-burst 0 --history-files 1000 --sqlite-writes 500`. The Raw writer and storage settings were the same; only the packet-count bound changed from 2,048 to 8,192. The independent queue-payload cap remained 32 MiB.

| Queue bound | Send calls / duration | App received | App admitted | App drops | Raw flushed | Host `RcvbufErrors` delta | Queue high-water | Queue payload high-water |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2,048 packets | 100,000 / 20.17 s | 99,975 | 88,515 | 11,460 | 88,515 | 25 | 2,048 | 1,048,576 B |
| 8,192 packets | 100,000 / 20.00 s | 100,000 | 100,000 | 0 | 100,000 | 0 | 1,053 | 539,136 B |

For the 2,048 run, 25 `sendto` successes were not observed by the application and host-global `RcvbufErrors` increased by 25. Separately, 11,460 received datagrams were rejected by the full application queue. For the 8,192 run, every send was observed and durably flushed with no measured host-global UDP errors or application drops. This supports the larger count window for the measured transient writer stalls; it does not establish a general capacity guarantee. The 32 MiB payload cap, 8,192-record count cap and bounded worker batch remain in force.

The 8,192 run used a requested 4 MiB UDP receive buffer; the OS reported 8 MiB effective. It recorded CPU at 25.6% host-normalized / 38.4% of the 2-core quota, working set 121,544,704 B, 22 threads, 232 file descriptors and 77,813,032 B data-root growth during the UDP case. Queue high-water was 1,053 packets / 539,136 payload bytes. The file-system overlay exposed neither physical write bytes nor IOPS.

## Parameterized Syslog trace matrix

Result: [28-case 8,192-queue trace JSON](perf-results/udp-trace-current-8192-full-matrix.json). Each row sent `requested PPS × 1 second` packets. The native absolute-clock pacer records actual wall-time rate and does not catch up after scheduling delays.

| Requested PPS | Achieved send PPS across 128/256/512/1,024 B | Send successes | App received | App drops | Raw persisted | Host `RcvbufErrors` delta |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 100 | 400 | 400 | 0 | 400 | 0 |
| 500 | 476–491 | 2,000 | 2,000 | 0 | 2,000 | 0 |
| 1,000 | 955–965 | 4,000 | 4,000 | 0 | 4,000 | 0 |
| 3,000 | 2,534–2,700 | 12,000 | 12,000 | 0 | 12,000 | 0 |
| 5,000 | 3,801–4,172 | 20,000 | 20,000 | 0 | 20,000 | 0 |
| 10,000 | 7,510–7,716 | 40,000 | 40,000 | 0 | 40,000 | 0 |
| 20,000 | 12,454–13,515 | 80,000 | 80,000 | 0 | 80,000 | 0 |
| **Total** | — | **158,400** | **158,400** | **0** | **158,400** | **0** |

Every persisted trace passed its checksum; the matrix reported zero missing sequence IDs, duplicates or order inversions. Maximum queue occupancy was 590/8,192 packets and 533,504/33,554,432 payload bytes. Maximum per-case working set was 118,661,120 B, 18 threads, 233 file descriptors and 29,360,125 B in the isolated data root. Effective-core-normalized CPU peaked at 38.3% in a one-second case. The sender did not reach the 10k or 20k requested rates, so those rows report results at the measured lower send rates, not successful 10k/20k qualification.

The same 28-case native-pacer PR #8 baseline had 158,400 local `sendto` successes, 154,363 application receives/Raw flushes, zero application queue drops, and 4,037 host-global `RcvbufErrors` (equal to the aggregate send-to-application gap). The current batched writer / 8,192 queue matrix received and flushed all 158,400; Service-process `write` syscalls across the cases fell from 105,590 to 7,036 (93.3% fewer). This is a measured syscall-count change; it does not mean physical IOPS fell by that amount or prove the container's file-system latency will transfer to another host.

## Sustained and burst runs

The [30-second-requested steady trace](perf-results/udp-trace-current-8192-30s-5k-512.json) sent 150,000 512-byte payloads. The native sender's actual elapsed time was 55.64 s (2,696 PPS), because the shared host could not keep the requested 5k pacing. All 150,000 were received, enqueued and persisted; no application drops, missing IDs, duplicates, order inversions or host-global UDP errors were recorded. Queue high-water was 265 packets / 135,680 payload bytes. Process CPU was 15.2% host-normalized / 22.8% of the quota, working set peaked at 228,950,016 B, 18 threads and 232 file descriptors. The Raw sync window measured p50 0.116 ms, p95 1.447 ms, p99 13.187 ms and max 187.539 ms; segment seal p50 7.131 ms, p95 19.436 ms and max 3,450.737 ms; SQLite segment registration p50 1.066 ms, p95 2.366 ms and max 6.970 ms. The long seal outlier is real for this run and the container does not let us isolate whether hashing, overlay I/O or host scheduling dominated it.

The [50,000-datagram immediate burst trace](perf-results/udp-trace-current-8192-burst.json) is an overload diagnostic, not a passing normal-load test. For each payload length, the sender's local `sendto` succeeded 50,000 times. By length:

| Payload | Actual send PPS | App received | Host-global `RcvbufErrors` delta | Application queue drops | Raw persisted |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 128 B | 401,992 | 18,787 | 31,213 | 0 | 18,787 |
| 256 B | 325,232 | 15,372 | 34,628 | 6,668 | 8,704 |
| 512 B | 214,712 | 26,195 | 23,805 | 12,611 | 13,584 |
| 1,024 B | 135,196 | 33,228 | 16,772 | 24,496 | 8,732 |

Each host-global error delta exactly matched that case's sender-success minus application-received count, while application drops are separately reconciled as received minus admitted. The 256/512/1,024-byte burst queues reached their 8,192 packet high-water; the 128-byte case peaked at 6,133 after most datagrams had already been lost before application receive. These counters establish two observable loss stages for this isolated burst run; because the kernel metrics are host-global they are not a per-socket proof. The JSON retains exact missing sequence IDs as inclusive/exclusive `missingSequenceRanges`, along with the count, dropped records and persisted IDs' integrity summary.

## Raw/SQLite batching and recovery measurements

Raw NDJSON is serialized into reusable UTF-8 staging memory without constructing an intermediate Base64 string. A sync is triggered at 256 records, 256 KiB, 100 ms maximum batch age, or a 4 MiB segment boundary. Batches still call `FileStream.Flush(true)` and SQLite still uses `synchronous=FULL`; no durability setting was weakened. A sealed segment is hashed, renamed and registered in a short SQLite transaction once per 4 MiB segment, not once per packet. Normal shutdown drains the admitted queue and seals the segment.

The 100,000-record 8,192-queue probe recorded Raw sync p50 0.095 ms, p95 0.815 ms, p99 2.919 ms, max 5.439 ms; segment seal p50 9.617 ms, p95 22.905 ms, max 22.905 ms; SQLite segment-registration p50 1.392 ms, p95 6.311 ms, max 6.311 ms. A separate 500 single-row SQLite FULL-sync probe measured p50 0.0060 ms, p95 0.0083 ms, p99 0.0322 ms and max 0.481 ms on this container.

With 1,000 pre-indexed, trusted historical 11,016-byte Raw files (11,016,000 B), startup scanned 1,019 paths and trusted-skipped 1,019, read/hashed 0 file-content bytes, and reported Catalog recovery in 32 ms; complete process restart wall time was 531 ms. This demonstrates the checkpoint fast path for this fixture size, not adversarial metadata mutation detection or a production directory-scale guarantee. A later low-priority audit is still required to detect same-size/same-timestamp edits.

### G2 branch Syslog regression sample

Command: `python3 tests/perf_baseline.py --packets 100000 --payload-bytes 512 --rate 5000 --queue-burst 0 --history-files 1000 --sqlite-writes 500 --output docs/perf-results/g2-h3c-raw-writer-regression.json`. This is one current-branch regression sample after Raw Writer generalization; it is not a statistically sufficient before/after claim and it does not measure SSH/device collection.

The paced sender completed 100,000 local `sendto` calls in 20.000 s. The Service received, admitted and flushed all 100,000 with zero application drops. The requested 4 MiB receive buffer reported 8 MiB effective; the host-global `RcvbufErrors` counter did not increase in the test interval. Queue high-water was 275 packets / 140,800 payload bytes. Process CPU was 25.95% host-normalized / 38.93% of the two-core quota; working set was 113,979,392 B, 21 threads and 231 file descriptors. The data root grew 77,813,033 B during the Syslog load. Raw sync latency was p50/p95/p99 0.0861/0.3984/0.7898 ms (max 1.9238); segment seal p50/p95 7.1079/22.2286 ms; SQLite registration p50/p95 1.2838/5.3368 ms. A separate 500-commit SQLite FULL-sync probe measured p50/p95/p99 0.0088/0.0148/0.1039 ms (max 0.3604). At startup, 1,019 indexed files were scanned/trusted-skipped, 0 content bytes read or hashed, Catalog recovery took 32 ms and full restart wall time was 562 ms. Physical IOPS were unavailable. The exact environment and full counters are in the linked JSON result.

## Durability and limits

The maximum application queue payload is 32 MiB, with an independent 8,192-record ceiling. In addition, the writer can hold a batch of up to 256 KiB of serialized NDJSON and one maximum UDP datagram outside that queue; packets not yet read by the Service remain in the OS socket buffer. Normal stop drains accepted work and seals it. A process kill or power loss can lose kernel-buffered datagrams, queued packets and staged bytes not yet covered by `Flush(true)`; a hardware controller may also misrepresent flush completion. Previously synchronized `.open` prefixes are reconciled and damaged tails keep separate hashed recovery evidence. This design does not promise zero loss on power failure.

Current measurements cover up to 150,000 traced records and one 100,000-record 5k-PPS run, plus 50,000-record burst groups. They do not establish 1,000,000-record throughput, 72-hour/7-day stability, low-configuration industrial performance, physical IOPS, real disk exhaustion, power-loss durability, or Windows-target performance. CPU/RSS figures varied across shared-host runs; a single idle sample is not a long-term average. No low-configuration target is marked as meeting performance goals.

Artifacts and their exact environment/output are under [perf-results/](perf-results/). [manifest.json](perf-results/manifest.json) records the baseline HEAD and SHA-256 hashes of the measured source files and result files. The target industrial host profiles and real Windows target systems remain `PENDING_REAL_ENVIRONMENT`.
