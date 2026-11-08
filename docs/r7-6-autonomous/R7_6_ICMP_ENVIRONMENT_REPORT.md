# R7.6 ICMP environment comparison

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
