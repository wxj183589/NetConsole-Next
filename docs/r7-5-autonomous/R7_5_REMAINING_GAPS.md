# R7.5 Remaining Gaps

Overall status: **PARTIAL** as of 2026-11-08.

| Area | Status | Remaining evidence or condition |
| --- | --- | --- |
| Candidate build and current runtime | PARTIAL | Candidate is live and healthy on 18771. Offline candidate gate is partial because loopback Traceroute received `ICMP_PERMISSION_DENIED`. Elevated isolated Windows acceptance is still required for that gate. |
| AC1 / AC2 collection | PARTIAL | R7.4 evidence proves read-only product collection and independent R/M and R/B observations at that capture time. No new R7.5 device capture was run. |
| FIT-AP | BLOCKED | AC1 accepted `wlan ap-execute all exec-console enable`, but effective state and FIT login are unverified. Needs an approved credential; no guessed login or save. |
| SW SSH | BLOCKED | Its only enabled inventory-linked profile previously failed authentication. A valid profile is needed before another attempt. |
| Dual AC HA | NOT_TESTED | No role change, failover, failback or business continuity measurement. Preserve recovery path before any future switching. |
| Long Ping | PARTIAL | Service and Web regression coverage passes. No real long-duration run or retained-history soak was performed. |
| Fault center | PARTIAL | Harness coverage passes. No induced live device fault, automatic recovery or real incident cycle was run. |
| Configuration restore | PARTIAL | Preflight/readiness and synthetic flows pass. Hardware writer, readback hash, business probe and rollback remain unverified. |
| Windows SCM | NOT_TESTED | Candidate runs as a process; elevated SCM install, upgrade, recovery and uninstall were not tested. |
| Feature parity | PARTIAL | Mesh analysis, full rail reports, broader Excel workflows and remaining original feature parity remain open. |
| Long-running stability | NOT_TESTED | This session is not a 24-hour soak. |

No device changes, remote pushes, merges or release publications occurred in this checkpoint.
