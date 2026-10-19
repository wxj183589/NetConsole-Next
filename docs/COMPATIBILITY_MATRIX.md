# Runtime and OS compatibility matrix

Current Service and Local Manager target `net10.0`. Build success on a current runner is not runtime support for an older Windows release.

| Environment | Build / automated evidence | Real runtime / service evidence | Status |
| --- | --- | --- | --- |
| Ubuntu GitHub runner | Service, Local Manager, scheduler test harness, Syslog smoke and G1 recovery tests | Linux process-level behavior covered; not a production support claim | `CI_VERIFIED` |
| Windows GitHub runner | Service, Local Manager, and scheduler harness passed in Actions run 39258222423 | Windows Service installation, account ACL, SCM recovery and upgrade not exercised | `COMPATIBILITY_PENDING` |
| Windows 10 x64 | No target-machine evidence yet | Service lifecycle, ProgramData permissions, installer/uninstaller and long run pending | `COMPATIBILITY_PENDING` |
| Windows 11 x64 | No target-machine evidence yet | Service lifecycle, permissions, installer/uninstaller and long run pending | `COMPATIBILITY_PENDING` |
| Windows Server 2012 / 2012 R2 | No target-machine evidence yet | Runtime support not declared; requires supported-host/runtime research and a real VM | `BLOCKED` pending runtime decision |
| Windows 8 / 8.1 x64 | No target-machine evidence yet | Runtime support not declared; no compatibility shims or security-check bypass | `BLOCKED` pending runtime decision |
| Windows ARM64 | No build or runtime matrix yet | Not tested | `NOT_STARTED` |
| Linux ARM64 / Kylin | No build or runtime matrix yet | Not tested | `NOT_STARTED` |

The release target remains Windows x64. Do not advertise Windows 8/8.1 or Server 2012/R2 support based on `net10.0` compilation. Resolve the .NET host support policy before selecting an alternate runtime; do not introduce an obsolete runtime solely to make the matrix appear green.
