# R7.5 Runtime Handoff

Date: 2026-11-08. Status: **PARTIAL**.

Branch: `codex-A/netconsole-next-r3-dual-ac`. Product code at integrated HEAD `006f79b60fff5c7cce60ad046861c884c7113571`; R7.5 reports and runtime-control support are delivered in a separate local commit. No push, remote merge or release publication.

Current Service: `http://127.0.0.1:18771/`, PID 17964, launched from `build/r75-candidate-verified-006f79b-r2/NetConsole.Next.Service.dll`. It uses `test-data/simulator-live-20261105`, loopback Syslog UDP 15514, H3C collection enabled, and the candidate-bundled Netmiko Worker. Health is `HEALTHY`; SQLite integrity is `ok`; Raw shows 128 finalized / 0 incomplete / 0 missing and no recovery issues. Vite remains at `http://127.0.0.1:5174/`, PID 32812.

Pre-upgrade checkpoint: `test-data/r75-runtime-checkpoint-20261108/catalog.db`, SQLite integrity `ok`, SHA256 `404612c2f12b2e83ce4965d53e9a74b99a464a551861cff41bc9f7a22510cf08`. It is a single database checkpoint, not a full data-root copy.

Candidate offline E2E is PARTIAL only at the required Traceroute live ICMP check (`ICMP_PERMISSION_DENIED`). All named synthetic service, browser and data-integrity checks recorded in the deployment/UI reports passed. Keep the current instance running. Real HA, FIT-AP login, hardware restore/rollback, elevated SCM and long-duration soak remain outstanding.
