# R7.4 Runtime Handoff

Date: 2026-11-08. Status: **PARTIAL**.

Product branch is `codex-A/netconsole-next-r3-dual-ac`; the R7.4 product commits are `0a39c5e`, `53af103`, `158eba3` and `a86731b`. This report is committed separately. No push, merge or release was performed.

The existing Service is healthy at <http://127.0.0.1:18771/> (PID 33036, `build/r73-service/NetConsole.Next.Service.dll`) and Vite responds at <http://127.0.0.1:5174/> (PID 32812). Keep both running. The integrated Service uses the isolated Next root `D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`; do not copy it or use an original NetConsole data root. The Service currently running does not contain the new R7.4 backend code.

Device status: AC1/AC2 have fresh product read-only capture and AP `R/M`/`R/B` observations. SW authentication failed with its only inventory-linked profile and was not retried. FIT Telnet reached a password prompt without submitting credentials. Per the user's explicit request, AC1 accepted `wlan ap-execute all exec-console enable` in probe view; no save was issued. The running config and available display help did not expose effective-state readback, so FIT remote login remains unverified. This is one authorized running-config change on AC1.

Tests: Web 353/353 and typecheck passed; full Service Release harness exited 0; LocalManager readiness 3/3, portable runtime 9/9 and doctor 13/13 passed; workspace paths 4/4 passed. Browser E2E on the R7.4 integrated backend, real SCM, HA switch/failback, device restore/rollback and long soak remain untested.

Next: preserve the healthy current processes; obtain an authorized replacement SW credential through the existing profile workflow before retrying; then capture SW version/interface/LLDP with the product task path. Build the R7.4 Service and static bundle into an isolated Workspace output before browser/API E2E. Continue HA, restore, SCM and long-running work only when their explicit recovery/host prerequisites are met.
