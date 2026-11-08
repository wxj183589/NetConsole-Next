# R7.7 Windows deployment workstream

## Final candidate integration

Status remains **PARTIAL**. The same final candidatebuild/r77-candidate-api completed the entire DOTNET_DLL_EXPLICIT offline gate with EXIT0 (reports/r77-offline-dotnet.log), including real Windows loopbackICMP, bundled Worker reconnect, Linux terminal, configuration kinds, task evidence and durable schedules. APPHOST complete gate EXIT1 (r77-offline-apphost-final.log), ICMP_PERMISSION_DENIED; independent schedule/configuration checks passed. This is launch-context evidence, not a claim of apphost/SCM installation success.

Shared runtime was gracefully stopped by exact oldPID41856/DLL, a singleSQLite checkpoint created, then restored using final explicitDLL atPID21540/18771, bundled Worker andSyslog15514. SQLite164 Raw/19 assets verified. No SCM/firewall/security policy changed; installation/upgrade/rollback/uninstall acceptance and8–24h remainNOT_TESTED.

## 此前工作流记录

2026-11-08; Agent codex-A; branch `codex-A/netconsole-next-r3-dual-ac`.

The product deployment command `NetConsole.Next.Service --icmp-diagnose` now exposes both direct .NET ICMP API and production system Traceroute outcomes within the same process. This helps distinguish apphost restrictions from a separately launched `dotnet.exe` probe; it avoids recommending elevation of the whole product based on an unrelated process's success.

The command retains its pre-Host/pre-mutex/pre-data behavior, fixed loopback destination and sanitized error details. It changes no SCM configuration, firewall, user/group membership, process token or global security policy. Existing LocalManager start/stop/upgrade/rollback behavior is unchanged.

Build, 4/4 deployment-diagnostic regression results, saved apphost/DLL evidence and the remaining full-Service ICMP boundary are recorded in [R7_7_TRACEROUTE_REPORT.md](R7_7_TRACEROUTE_REPORT.md).

SCM installation/start/stop under the intended service account, real installation/upgrade/rollback/uninstall acceptance and 8–24-hour soak were not performed by this workstream. Those remain pending; this diagnostic change is not evidence that they passed. Full offline candidate gating belongs to the integration window because the shared Service uses a global single-instance mutex.

**Status: PARTIAL.** Deployment self-diagnosis is implemented; SCM and final candidate acceptance remain separate checks.
