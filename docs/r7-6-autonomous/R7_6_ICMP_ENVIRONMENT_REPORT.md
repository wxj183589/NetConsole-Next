# R7.6 ICMP environment comparison

## 2026-11-08 最新集成检查点

本批已完成丢失采样历史上下文、实际Raw完整性复核并部署。产品56d16e3/6e48a0a，夹具与验收d99e103/8cd1e2f/0dd6a14。最新候选build/r76-loss-context-8cd1e2f，ProductVersion1.0.0+8cd1e2f96038acef6c63181ad49cb6d7c4920403，Service18771/PID41856 HEALTHY；Vite5174/PID32812保留。

当前Web36files/391tests、typecheck、Service全量EXIT0、loss37/37、候选发布、loss HTTP18与双视口Chrome18、最新候选广覆盖浏览器PASS。SQLite/140Raw逐项长度SHA256、单DB检查点与19静态资源均通过。本批未重跑完整离线门禁，之前ICMP_PERMISSION_DENIED失败未解除；真实客户端/AP关联为UNKNOWN，HA/设备恢复/SCM/8–24h仍未验。

完整当前制品身份、证据路径、数据影响和续跑步骤见[R7_6_PING_LOSS_EVIDENCE_REPORT.md](R7_6_PING_LOSS_EVIDENCE_REPORT.md)。本节取代下文旧候选/PID/测试数作为当前值；下文为此前批次实际证据，未重跑的检查不得视为本批通过。

## 此前批次证据

2026-11-08; Agent codex-A; candidate `build/r76-candidate-integrated-91d0a63`, ProductVersion 7099775. Investigation source baseline `30e3174`; no Service started and no shared repository/runtime/security settings changed.

The actual opt-in command is `--icmp-diagnose`. Program rejects extra arguments and returns before the single-instance mutex, runtime settings, Host and data initialization. It fixes the target to 127.0.0.1.

## Same-candidate observations

Each cell below represents two executions. All used the same candidate bytes and cwd. The offline environment duplicates the test's NETCONSOLE_NEXT_/PYTHON prefix filtering, System32-only PATH, isolated TEMP/TMP and Production settings. The final row also matches its CREATE_NEW_PROCESS_GROUP flag.

| Environment | APPHOST diagnosis | Explicit dotnet DLL diagnosis | Python direct system32 tracert |
| --- | --- | --- | --- |
| Inherited | FAILED native 5, 2/2 | PASS, 2/2 | Reached loopback, 2/2 |
| Offline isolated | FAILED native 5, 2/2 | PASS, 2/2 | Reached loopback, 2/2 |
| Offline isolated + new process group | FAILED native 5, 2/2 | PASS, 2/2 | Reached loopback, 2/2 |

Evidence: Workspace `reports/r76-icmp-env-20261108-221250/summary.json`, including EXE/DLL SHA256, process IDs, native exception codes, timing, and changed environment names without arbitrary environment values. All diagnostic reports say hostStarted=false and dataRootAccessed=false; the supplied nonexistent data-root path remained absent. Earlier two-environment run is `reports/r76-icmp-env-20261108-221017/summary.json`.

## Native error location

A separate minimal console at Workspace `diagnostic/r76-icmp-stage` links the unchanged WindowsSystemTraceroute and NetworkTraceroute source from this worktree. Its processStarted callback proves tracert launches. APPHOST produced a child PID, then ParseHop line 167 threw `System ICMP traceroute transmit failed` with inner Win32Exception code 5. Explicit dotnet DLL produced a child PID and REACHED hop. Saved logs: `reports/r76-icmp-env-20261108-221250/stage-apphost.log` and `stage-dotnet.log`.

This error is a parsed native tracert transmit failure, not evidence that Process.Start was denied. The existing parser correctly preserves native error 5. A product change that returned success or retried through a different executable would hide the actual deployment boundary. No such change was made.

The full offline DOTNET_DLL_EXPLICIT Host gate still failed ICMP_PERMISSION_DENIED, according to the independently produced `reports/r76-offline-dotnet.log`. The environment-only and process-group comparisons do not reproduce that Host failure. They do not establish its underlying security policy or thread/runtime cause; diagnosis PASS must not replace full Host acceptance. No system security policy, elevation, Service restart, device command or business data access was used in this investigation.

Reproduce from the repository with `python tests/r76_icmp_environment_probe.py --candidate <candidate-directory>`. The script runs only fixed-loopback diagnostics and writes bounded reports under the Next Workspace. It is a diagnostic comparison, not an acceptance gate that permits deployment.
