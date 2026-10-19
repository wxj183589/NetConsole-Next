# Test evidence

Evidence is scoped to the stated code and environment. CI or simulation does not imply field acceptance.

| Evidence | Environment / command | Current result | What it does not establish |
| --- | --- | --- | --- |
| Service build | Linux / `dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release` | `CI_VERIFIED`, 0 warnings / 0 errors (2026-10-19 local run) | Windows runtime compatibility or long-run stability |
| Local Manager build | Linux / `dotnet build src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj -c Release` | `CI_VERIFIED`, 0 warnings / 0 errors (2026-10-19 local run) | Windows SCM permissions or real service operations |
| Scheduler lifecycle | Linux local run: `dotnet run --project tests/NetConsole.Next.Service.Tests -c Release` | Local run passed; Windows and GitHub Actions result for this branch pending | Real SSH/SNMP device protocol behavior |
| UDP smoke | `python3 tests/smoke.py` | `CI_VERIFIED`; loopback, payload byte round-trip, seal and catalog | Kernel/network losslessness |
| G1 recovery integration | `python3 tests/g1_recovery.py` | `CI_VERIFIED`; kill/restart, malformed tail evidence, orphan/missing reconciliation, repeat recovery, single instance, symlink failure preservation, task lifecycle/resource fields | Sudden power loss or full-disk behavior |
| GitHub Actions G1 | Run 39257049495 and 39257042204 | Linux recovery/integration job and Windows Service/Manager build both passed on the G1 evidence-preservation commit | Windows Service install/start/stop on target releases |
| GitHub Actions scheduler harness | Current follow-up branch | Pending push/PR run | Windows Service install/start/stop on target releases |
| Dependency audit | `dotnet list ... package --vulnerable --include-transitive` | No known vulnerable packages reported by current NuGet source (2026-10-19) | License compliance or future dependency updates |
| Live device and target Windows | No access in this run | `PENDING_REAL_ENVIRONMENT` | All field/compatibility claims |
