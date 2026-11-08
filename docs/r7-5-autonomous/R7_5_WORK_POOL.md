# R7.5 Work Pool

Date: 2026-11-08. Branch: `codex-A/netconsole-next-r3-dual-ac`. Work began at `bae70ce`; three workstreams integrated through `006f79b`.

| Stream | Owner | File ownership | State | Acceptance |
| --- | --- | --- | --- | --- |
| A Deployment/runtime | Integrator | `tests/r70_runtime_control.py`, candidate deployment evidence, R7.5 deployment/runtime reports | PARTIAL | New candidate is live on 18771; health, SQLite, Raw, HTTP and browser pass. Offline candidate gate is partial because Windows denied loopback ICMP in Traceroute. |
| B Device connection and collection | Integrator | Read-only probe evidence under Workspace `reports`; no source changes until a specific defect is isolated | PARTIAL | Existing AC1/AC2 read-only evidence remains valid for its capture time; no new device connections in this closure. SW authentication failure and FIT credential gap remain. |
| C Wireless business | Integrator | R3 contracts and R7.5 wireless report; shared wireless Service files reserved for coordinated integration | PARTIAL | Independent AC observations and UI evidence preserved; no HA transition or new live capture in this closure. |
| D FIT-AP | Integrator | Read-only AC/FIT probe evidence; no shared production code until endpoint behavior is proven | BLOCKED | AC1 accepted Exec Console command, but effective state and FIT login remain unverified; no save or guessed credentials. |
| E Ping/fault center | `r75_long_ping` and `r75_fault` isolated worktrees | Long Ping owner: `NetworkDiagnostics.cs`, `NetworkQuickPing.cs` and their Service tests. Fault owner: FaultCenter/OperationalHealth Service files and C# tests. | PARTIAL | Integrated harness passes Long/Quick Ping and Fault Center suites; real long-duration operation and induced device fault/recovery remain untested. |
| F Configuration management | Integrator | Config services/tests after a concrete gap is verified; do not overlap fault or diagnostic ownership | PARTIAL | Harness and candidate synthetic backup schedules pass; hardware restore, rollback and readback remain unverified. |
| G Feature migration | Integrator | Original-to-Next matrix review and selected narrow product task after audit | PARTIAL | Matrix review recorded; broader Mesh, rail reports, Excel and remaining parity work stay open. |
| H Operations UI | `r75_ui` isolated worktree | Dashboard/Tools/Wireless views and their Vue tests | PASS | Wireless evidence is secondary to the readable AP state; full Web tests, typecheck and real Chromium checks at both target resolutions pass. |

## Integration Rules

- Agents use Git worktrees under the Next Workspace and must not edit the primary checkout.
- Shared API, runtime settings, Program startup, build output, static assets and reports remain integrator-owned.
- No push, remote merge, release publication or destructive cleanup.
- Existing R7.4 untracked assets remain protected; only generated R7.5 output in its isolated build directory is eligible for candidate runtime use.
- Device configuration mutation remains limited to the already authorized AC1 Exec Console command; no startup save, HA switching or credential retries are authorized by this work pool.

Final integrated product HEAD: `006f79b60fff5c7cce60ad046861c884c7113571`. The release candidate is `build/r75-candidate-verified-006f79b-r2`; candidate reports must retain the Traceroute `ICMP_PERMISSION_DENIED` result as PARTIAL.
