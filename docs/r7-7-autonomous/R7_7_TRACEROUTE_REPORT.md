# R7.7 Traceroute / ICMP investigation

## Final complete product gate (supersedes the earlier pending status)

Same final candidatebuild/r77-candidate-api:
- **PASS** DOTNET_DLL_EXPLICIT full offline gate EXIT0, including actual Windows loopback TTL1/reached and persisted/restarted authorized diagnostic history; reports/r77-offline-dotnet.log.
- **PARTIAL** APPHOST full offline gate EXIT1: statePARTIAL/outcomeICMP_PERMISSION_DENIED; reports/r77-offline-apphost-final.log. Independent schedule/configuration subgates passed.
- Earlier cfc801c APPHOST gate also failed; it is preserved rather than replaced by narrow diagnostic success.

This supports using the existing explicitDLL launch for the local running candidate; it does not identify the Windows enforcing component, change policy, validate SCM identity, or prove APPHOST fixed. Diagnostic unit4/4 and entire Service harnessEXIT0. No TCP connect was relabeled asTTLTraceroute.

## 此前工作流记录

2026-11-08; Agent codex-A; branch `codex-A/netconsole-next-r3-dual-ac`; source baseline `b4d942e0558de5733180e878c495305c391a363f`.

## Product change

`IcmpDeploymentDiagnostics.RunAsync` now reports a bounded, fixed-loopback `.NET Ping` result in `nativeIcmp` alongside the existing production Traceroute result. Both execute within the same process and token. The new result includes status and the sanitized exception chain/native error code. It contains no arbitrary exception message, credentials, data-root content or device target.

The primary production result and exit code remain authoritative. A successful direct ICMP API check cannot turn failed system Traceroute into success. No launch-mode fallback, elevation, security-policy changes, TCP substitution or fabricated hops were added. The command still returns before Host, mutex and data initialization. The direct check has a 2-second deadline; the existing production check retains its 5-second deadline and 2-second trace budget.

## Current bounded evidence

Isolated diagnostic project: Workspace `diagnostic/r77-icmp-native`. It links the actual production `NetworkTraceroute.cs`, `WindowsSystemTraceroute.cs`, `IcmpDeploymentDiagnostics.cs` and the diagnostic regression tests. Its bin/obj are isolated from the shared repository build.

| Same linked source / ordinary token | Direct .NET ICMP API | Production system ICMP tracert |
| --- | --- | --- |
| `Probe.exe` apphost | FAILED, native code 5 | FAILED, native code 5 |
| Explicit `dotnet Probe.dll` | PASS, loopback success | PASS, destination reached |

Saved evidence in that directory: `apphost-diagnose.json`, `dotnet-diagnose.json`, `binary-sha256.json` and `host-context-apphost.log` / `host-context-dotnet.log`.

The explicit DLL also succeeds before/after `WebApplication.CreateBuilder`, `builder.Build`, `app.StartAsync`, a `Task.Run` call and an actual HTTP request. The Host reproduction uses an OS-assigned loopback port and never starts the product Service, acquires its mutex or accesses business data. Apphost fails the corresponding checks. Thus the parser, child-start alone, thread pool and basic ASP.NET request context do not explain the observed denial.

This establishes a process/launch-context-sensitive native ICMP restriction; the exact enforcing Windows or endpoint-security component has not been identified. It does not prove that the complete product Host can trace successfully. Prior R7.6 complete offline gates failed with `ICMP_PERMISSION_DENIED` in both launch modes; that failure remains open until the R7.7 integration owner reruns the entire gate on the final candidate.

## Validation and limits

- Isolated linked-source Release build: PASS, 0 warnings / 0 errors.
- `IcmpDeploymentDiagnosticsTests`: 4/4 PASS; actual status, sanitized native code, independent native success with primary failure, and independent native failure with primary success are preserved.
- Apphost and explicit DLL diagnostic evidence were rerun on this host after the change.
- Complete Service suite, published candidate and complete offline gate are integration-owner checks and were not run independently by this workstream.
- No shared Service/Vite process was stopped, no simulator/device command issued, and no production/legacy/development business database accessed or copied.
- TCP connect is not TTL Traceroute; a TCP mode was not invented without observed TTL-expired evidence.

**Status: PARTIAL.** Improved deployment diagnosis is implemented and locally verified. Full-product ICMP permission failure is not closed by these bounded probes.
