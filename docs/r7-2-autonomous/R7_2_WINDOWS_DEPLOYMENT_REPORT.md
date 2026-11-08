# R7.2 Windows deployment and local operations

Date: 2026-11-08. Original workstream baseline `850b7eb7e92872bb4899ef52e118fa59cc98c64c`. Continuation baseline `065a749c4dc7e81280010afc1293de004ea895ff` plus the retained R7.2 working changes, branch `codex-A/netconsole-next-r3-dual-ac`.

Status: **PARTIAL**. Product operations code and targeted checks pass. Actual SCM installation, service-account access, automatic startup, crash recovery, upgrade/fallback and uninstall acceptance remain **NOT_TESTED / ACCESS_DENIED** in this non-administrator session. Interactive execution is reported separately.

## Product changes

- `NetConsole.Next.LocalManager.exe` without arguments presents one Chinese menu for start/open, stop, restart, diagnose and status. `launch` uses the installed SCM service when present, otherwise an owned apphost process with built Web. No Vite/Node terminal is required.
- Existing SCM configuration is read from that service's own registry environment, including data root, Web port and binary. SCM access denial does not silently create a second instance.
- Explicit `portable-start`, `portable-stop`, `portable-restart`, `portable-status` accept a small optional JSON settings file. Program/data directories must be separate, absolute local paths; legacy data roots and reparse ancestors are refused.
- A genuinely empty root receives the existing `NETCONSOLE_NEXT_V1` marker before manager state. Foreign nonempty unmarked roots are preserved and rejected. Status/stop do not create absent roots.
- The receipt verifies PID, process start time, exact executable and configured port. A random current-user-only named pipe asks the interactive Host to stop gracefully; SCM instances ignore this optional pipe. Normal stop timeout does not kill the process. Failed-start cleanup is confined to the newly created child.
- A detached Windows spawn explicitly refuses inherited handles and has no console standard handles; it keeps service diagnostics in rolling logs and prevents the service from holding a captured LocalManager stdout pipeline open after the manager exits.
- Port preflight does not stop its owner. Startup requires a bounded health response from the exact spawned PID. Existing rolling logging is reused (14 × 5 MiB), with read-only capacity/log diagnosis: warn below 1 GiB or 5% free, inspect at most 15 top-level log files and never prune business data.
- `scripts/update-service.ps1` switches between separate version directories, preserving the existing service environment/data. It verifies actual SCM binary/root/port and healthy instance/PID/SQLite before switching, checks the same instance after restart, and attempts prior-binary fallback on failure. The same entry selects a retained older binary for rollback. Schema compatibility is required; no database downgrade/copy is attempted.
- Existing installation retains auto-start, virtual identity/ACL, service-local environment and restart delays 5/15/60 seconds. Existing uninstall retains binaries/data. Their real SCM acceptance is not inferred from code review or PlanOnly.

Usage: [Local Manager operations](../LOCAL_MANAGER_OPERATIONS.md).

## Current verification

Output is isolated under `D:\study\NetConsole-Next-Workspace\build\r72-operations` and test-data under the Next Workspace.

| Check | Result |
|---|---|
| LocalManager Release build | PASS, 0 warnings / 0 errors |
| Existing LocalDeploymentDoctor harness | 13/13 PASS, including real Junction/ACL refusal, read-only SQLite, matching instance, worker import/close, bounded/offline/invalid responses |
| Portable runtime boundaries | 9/9 PASS: absent root, foreign root, real TCP port conflict, first marker, corrupt receipt, PID/start-time mismatch, disk warning, bounded log inspection and settings boundaries |
| Service update PlanOnly | 5/5 PASS, no SCM/data creation; separate version plan, overwrite/legacy/foreign-root refusal and retained data |
| Actual apphost lifecycle | PASS: start/idempotent start, static Web, new PID after restart with same instance, graceful stop, retained sentinel, read-only SQLite integrity=ok, no leftover children |
| Real SCM install/start/stop/restart/crash/upgrade/rollback/uninstall | NOT_TESTED — create-service access denied |

Commands:

```powershell
dotnet build src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj -c Release -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r72-operations -v minimal
dotnet run --project tests/NetConsole.Next.LocalManager.Tests/NetConsole.Next.LocalManager.Tests.csproj -c Release -p:ArtifactsPath=D:/study/NetConsole-Next-Workspace/build/r72-operations
.\tests\service_update_plan_tests.ps1
.\tests\portable_runtime_lifecycle.ps1 -ManagerExecutable <manager.exe> -ServiceExecutable <service.exe>
```

## Permission evidence and limits

Current identity `ROG-MB9\18358`, administrator role **false**. `OpenSCManager(null, null, SC_MANAGER_CREATE_SERVICE)` returns **Win32 5 (Access denied)** without creating a service. Evidence: `D:\study\NetConsole-Next-Workspace\reports\r72-operations\scm-permission.json`.

During evidence refresh, passing PowerShell `$null` directly to string P/Invoke arguments produced Win32 123 (invalid name). The probe was corrected to pass native null arguments inside C#; the actual permission result is Win32 5. No SCM mutation was attempted.

The R7.1 apphost ICMP permission problem is not bypassed by launching `dotnet.exe`; portable management uses the packaged service EXE. Network tool/device capability acceptance remains independent of successful startup.

The first actual apphost run found a stdout-handle inheritance defect: the manager exited after a healthy startup, but PowerShell's captured pipeline waited for the service's inherited output handle. The owned PID `81760` was gracefully stopped through its receipt/control pipe, without forced termination. The initially blocked lifecycle then failed its HTTP probe because the service had been deliberately stopped. Evidence is retained at `D:\study\NetConsole-Next-Workspace\test-data\r72-portable-lifecycle\1f33535202d1431eabf8723ff95ed892` (`failure.txt`, config, catalog, rolling log). The product was corrected with detached handle-free creation and the LocalManager harness/build re-passed.

The coordinated actual rerun **PASS** completed in 4.25 seconds including the LocalManager rebuild. Apphost `D:\study\NetConsole-Next-Workspace\build\r72-service\NetConsole.Next.Service.exe` started as PID `30716`, restarted as PID `31096`, retained instance `461c0e1d897f4eedbee9caf2c33a8f60`, and served the built Web on loopback port `60896`. Idempotent start, graceful stop, retained file, SQLite read-only `integrity_check=ok` and zero leftover child processes passed. Both owned processes are stopped and the global mutex window was returned to integration. Evidence: `D:\study\NetConsole-Next-Workspace\test-data\r72-portable-lifecycle\7e72905dfa484cf2925488372562899e\result.json`. H3C/Terminal were disabled for this isolated lifecycle; real worker import/close was tested separately by the doctor harness. No device traffic was generated.

A subsequent added foreign-root storage diagnostic regression exposed an uncaught `InvalidDataException` in the new diagnostic/CLI error handling. Both filters now explicitly handle that exception, returning a bounded failure instead of a stack trace; the complete 13+9 harness and 5 update-plan groups re-passed. No service lifecycle behavior was changed by this error-reporting fix.

No old NetConsole real data root was accessed or copied, no devices were connected, and no configuration was written to devices by this workstream. Existing Vite process remains outside these changes. No Push/Merge/Release performed.

## Continuation verification

At 2026-11-08 16:42 Asia/Shanghai, the continuation reran the LocalManager Release build with 0 warnings and 0 errors. The complete LocalDeploymentDoctor harness passed 13/13 and portable runtime boundary groups passed 9/9. Service update PlanOnly groups passed 5/5. These checks used only isolated Next Workspace fixtures and did not start a service or connect to devices.

The read-only native SCM access probe again returned Win32 5 for `SC_MANAGER_CREATE_SERVICE`; identity `ROG-MB9\18358` still has no elevated administrator token. The new observation is preserved at `D:\study\NetConsole-Next-Workspace\reports\r72-operations\scm-permission-continuation.json`, including `mutated=false`. SCM lifecycle acceptance remains pending. The apphost lifecycle PASS above belongs to the earlier recorded run and was not repeated during this continuation, avoiding interference with product integration ownership of the global mutex. The earlier Vite reference is historical; current frontend/backend process state belongs to the final integration handoff.

## Remaining acceptance

An administrator-controlled isolated SCM run must exercise service-specific ACLs, boot auto-start, crash restart, worker shutdown, real upgrade failure/fallback and uninstall retention using schema-compatible versions. Installation failure compensation still needs privileged failure injection. A non-administrator interactive lifecycle cannot close those gates.
