# R7.8 Windows Deployment and LocalManager Report

Date: 2026-11-09. Agent: codex-A / R7.8 deployment workstream.

## Delivered changes

- LocalManager health readiness now shares the caller's remaining deadline across HTTP headers and body reads. Previously each request received a fresh two-second timeout even when the overall readiness budget had almost expired.
- Caller cancellation remains cancellation. A final timed-out request preserves an earlier useful degraded-health diagnosis instead of replacing it with a generic timeout.
- Deployment doctor rejects string, boolean, null, array, object, out-of-range, fractional and non-positive health process IDs as structured `FAILED` / `HEALTH` results with no claimed process ID. Previously non-number values could throw an uncaught `InvalidOperationException`.

Files: `src/NetConsole.Next.LocalManager/LocalServiceReadiness.cs`, `LocalDeploymentDoctor.cs`; `tests/NetConsole.Next.LocalManager.Tests/LocalServiceReadinessTests.cs`, `Program.cs`.

## Validation

All commands run from the Next repository. Isolated .NET artifacts: `D:\study\NetConsole-Next-Workspace\build\r78-deployment-artifacts`, resolved through `tools/workspace_paths.py` before use.

| Check | Result |
| --- | --- |
| LocalManager build / `--help` | PASS, zero warnings/errors |
| Local service readiness | 6/6 PASS, includes slow response headers/body sharing a 120 ms overall deadline, caller cancellation and existing invalid-PID cases |
| Portable runtime boundaries | 9/9 PASS |
| Deployment doctor | 14/14 PASS, includes nine invalid process-ID variants, read-only catalog and worker imports/close handshake |
| PowerShell deployment script syntax | 7/7 PASS |
| Service installation plans | 27/27 PASS, no SCM/registry writes |
| SCM lifecycle offline plans | 5/5 PASS, actual SCM lifecycle NOT_TESTED |

Commands:

```powershell
dotnet build tests/NetConsole.Next.LocalManager.Tests --artifacts-path D:\study\NetConsole-Next-Workspace\build\r78-deployment-artifacts --nologo -v:quiet
dotnet D:\study\NetConsole-Next-Workspace\build\r78-deployment-artifacts\bin\NetConsole.Next.LocalManager.Tests\debug\NetConsole.Next.LocalManager.Tests.dll
dotnet build src/NetConsole.Next.LocalManager --artifacts-path D:\study\NetConsole-Next-Workspace\build\r78-deployment-artifacts --nologo -v:minimal
tests/validate_windows_scripts.ps1
tests/service_installation_plan_tests.ps1
tests/r73_scm_plan_tests.ps1
```

Early targeted runs exposed a final sub-millisecond probe replacing a previously observed degraded state. The fix preserves the useful prior state; the passing run above exercises the corrected behavior. Existing semantic timeout fixtures were increased from 30/80 ms to 300/500 ms; the separate slow-response regression retains its 120 ms deadline and requires completion within one second (the previous fixed two-second implementation fails this boundary).

## Fresh ordinary-token / AppHost evidence

`whoami /groups` reports Medium Mandatory Level (`S-1-16-8192`); Administrators is deny-only. Read-only `Get-Service` found no NetConsole service. Installation, actual SCM start/stop/restart/recovery, reboot survival and installed-service ordinary-user GUI flows remain NOT_TESTED. This work does not claim SCM acceptance from offline plans.

Fresh bounded `--icmp-diagnose` executions used the existing R7.7 published candidate in `build\r77-candidate-api`:

| Launch | Exit | Production ICMP | Native .NET Ping |
| --- | --- | --- | --- |
| `NetConsole.Next.Service.exe --icmp-diagnose` | 1 | FAILED, Win32 native error 5 | FAILED, native error 5 |
| `dotnet NetConsole.Next.Service.dll --icmp-diagnose` | 0 | PASS, Success | PASS, Success |

Both runs reported `hostStarted=false`, `dataRootAccessed=false`, .NET 10.0.13 X64, target 127.0.0.1 and TTL 1. These are fresh checks of the prior published candidate, not acceptance of a newly published R7.8 candidate. The enforcing Windows/security component remains unidentified. AppHost ICMP remains BLOCKED in this token/environment; no firewall, global security, privilege, service-hosting or launcher policy was changed. DLL success does not make AppHost pass.

## Runtime and data boundary

The shared Service was not stopped, restarted or replaced. A read-only health check after diagnosis reported PID 21540, `running`, `HEALTHY`, SQLite startup integrity `ok`. Tests used only bounded synthetic fixtures under Next Workspace and cleaned their own fixtures. No old NetConsole data root was read or modified. Original untracked Web assets were retained. No commit or push was performed by this workstream; parent integration owns review and commit.

## Remaining acceptance

- Actual SCM installation, upgrade rollback, service identity permissions, restart recovery and reboot acceptance require the authorized SCM-capable test environment.
- AppHost ICMP error 5 requires host security diagnosis; the pre-host report remains available without starting the host or touching data.
- Full-product 8–24 hour endurance and Windows installation GUI acceptance are not represented by these short tests.

## 最终主集成补充（2026-11-09）

主集成后续已受控升级，当前PID43524、build/r78-handoff-candidate；上文PID21540属历史。最终DLL窄ICMP诊断成功，Host未启动/数据未访问；reports/r78-handoff-offline-dll.log完整门禁仍EXIT1，Traceroute ICMP_PERMISSION_DENIED，计划备份等子门禁通过。前轮r78-closure-offline-dll.log同样失败。窄成功不覆盖完整失败，具体拦截原因未定位，未改全局安全/防火墙或提权绕过。SCM、安装GUI及8–24h仍未验。
