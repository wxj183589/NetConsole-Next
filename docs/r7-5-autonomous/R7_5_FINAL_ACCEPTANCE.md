# R7.5 Final Acceptance

Date: 2026-11-08
Agent: `codex-A`
Branch: `codex-A/netconsole-next-r3-dual-ac`
Integrated product HEAD: `006f79b60fff5c7cce60ad046861c884c7113571`
Overall: **PARTIAL**

## Accepted in this checkpoint

- Integrated three R7.5 workstreams: bounded Long Ping lifecycle/history and fault evidence; wireless AP state presentation with expandable collection evidence; runtime start readiness and deployment support.
- Web suite: 34 files / 353 tests passed; `pnpm typecheck` passed.
- Full Release Service harness exited 0. Emitted suites included Fault Center 23/23, Operational Health 16/16, Quick Ping 7/7, Config Restore Readiness 16/16, Wireless Runtime 14/14, device connection 13/13, Task Diagnostic Evidence 96/96, H3C Diagnostic Capture 30/30, Configuration Backup 11/11, Configuration Schedule 19/19 and Network Diagnostics 10/10.
- Candidate published to `build/r75-candidate-verified-006f79b-r2`. Bundled Netmiko 4.7.0 / Paramiko 4.0.0 and Telnet protocol smoke passed.
- Offline candidate E2E passed static UI/API, synthetic SSH login and command, diagnostics, reconnect, Linux WebSocket terminal, AC resources, configuration kinds, full diagnostic, diagnostic history and scheduled backup scenarios. The overall gate is **PARTIAL** because its required live loopback Traceroute returned `ICMP_PERMISSION_DENIED` on this non-elevated Windows host.
- Chromium against the deployed candidate passed dashboard chart, fault filter, configuration storage/history and wireless observation export at 1920x1080 and 2560x1440; no horizontal overflow or browser errors.
- Candidate is running at `http://127.0.0.1:18771/`, PID 17964, on the existing isolated Next data root. Health is `HEALTHY`; Syslog 15514 and H3C collection are enabled; SQLite integrity is `ok`; Raw reports 128 finalized, 0 incomplete, 0 missing, with no recovery issues.

## Not accepted as complete

- FIT-AP native Telnet authentication and effective AC Exec Console state are unverified. No device credentials were guessed and no save was issued.
- SW authentication remains failed using its sole enabled inventory-linked profile; no retry was made.
- Dual-AC failover/failback, real Long Ping soak, induced device fault/recovery, hardware configuration restore/rollback, Windows SCM lifecycle and 24-hour stability were not tested.
- Offline candidate Traceroute is PARTIAL due to `ICMP_PERMISSION_DENIED`; this gate is not promoted to PASS.
- Remote push, remote merge and release publication were not performed.

Data: no old NetConsole data root was accessed. A single pre-upgrade SQLite checkpoint is retained at `test-data/r75-runtime-checkpoint-20261108/catalog.db`; it passed `PRAGMA integrity_check`. No full data-root copy was made. Device changes this turn: none.

See deployment, device interop, wireless, FIT-AP, Ping/fault, configuration, feature parity, UI and runtime handoff reports in this directory. These reports are delivered in a separate local documentation commit.
