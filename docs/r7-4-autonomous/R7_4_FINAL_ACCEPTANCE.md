# R7.4 Final Acceptance

Date: 2026-11-08  
Agent: `codex-A`  
Branch: `codex-A/netconsole-next-r3-dual-ac`  
Current product HEAD before this report commit: `a86731b`  
Overall: **PARTIAL**

## Completed in this checkpoint

- Added bounded Long Ping through the existing diagnostic sampler, current target revalidation, cancellation, progress, durable result history and shared sample display. The limit is 10 seconds to one hour, at most eight devices and at most 3,600 samples. Quick Ping keeps its existing 120-second per-device budget.
- Fresh, Raw-verified `RUNNING_BACKUP` evidence now resolves `AP_OFFLINE`; it does not resolve `AP_CONFLICT` without qualifying multi-source evidence.
- Wireless overview uses the selected AP runtime projection rather than guessing from an AC member state. Dashboard alerts now link directly to fault details.
- Interrupted simulator configuration writes remain reviewable after Service reconstruction and are never replayed automatically.
- LocalManager waits for a bounded, healthy loopback Service response after SCM reports start.
- Product-only read-only tasks succeeded on AC1 and AC2. Both reported Comware 7.1.064 / WX5540H-HCL and fresh AP observations for the same identity, respectively `R/M` and `R/B`.

## Verification

- Web: 34 files, 353 tests passed; `pnpm typecheck` passed.
- Service: full Release harness exited 0. Fault Center 23/23, Quick Ping/Long Ping 7/7, simulator configuration 22/22, operational health 15/15, H3C fact publication 20/20, and remaining existing suites passed.
- LocalManager: readiness 3/3, portable runtime 9/9, deployment doctor 13/13.
- Workspace paths: 4/4. `git diff --check` passed before product commits.
- AC1/AC2 product tasks and Raw verification: **SIMULATOR_READONLY PASS**. Evidence: `../../../reports/r72-product-dual-ac-20261108-185931/summary.json`.
- SW current product task: **AUTH_FAILED** using its sole inventory-linked enabled profile; no alternate profile exists. No retry was made. Evidence: `../../../reports/r72-product-lldp-scope-20261108-185958/summary.json`.
- FIT-AP: **SIMULATOR_TCP PASS**, credential-free Telnet negotiation reached a password prompt; username/password were not sent. Evidence: `../../../reports/r71-device-20261108-190230/summary.json`.
- Windows SCM lifecycle: **NOT_TESTED**; existing host is not elevated. LocalManager test coverage does not substitute for SCM acceptance.
- Real HA switch/failback, full configuration restore/rollback, browser E2E against the new integrated build, and long-running soak: **NOT_TESTED**.

## Git and data

- Product commits: `0a39c5e`, `53af103`, `158eba3`, `a86731b`.
- No push, remote merge or release publish.
- Device configuration writes: 0. AC tasks appended read-only Tasks, Raw and Facts to the existing isolated `test-data/simulator-live-20261105` root. No original NetConsole data root was accessed.
- Existing untracked static assets under `wwwroot/app/assets` remain untouched and uncommitted.

The currently running Service is the prior `build/r73-service/NetConsole.Next.Service.dll` (PID 33036), with Vite PID 32812 on port 5174. The new R7.4 server code has not been deployed into that process. Do not claim the live Service exposes Long Ping until a separate isolated R7.4 instance is built and started.
