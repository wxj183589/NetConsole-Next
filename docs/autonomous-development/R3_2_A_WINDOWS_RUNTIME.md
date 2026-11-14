# R3.2-A Windows 运行环境与本地服务验收

更新时间：2026-11-15（Asia/Shanghai）

## 结论

```text
R3_2_A_STATUS=PARTIAL
SCM_RUNTIME=BLOCKED_ADMIN_PRIVILEGE
LOCAL_MANAGER=PASS
SERVICE_RUNTIME_BUILD=PASS
SERVICE_RECOVERY=NOT_VERIFIED
UPGRADE_DATA_PRESERVATION=NOT_VERIFIED
R3_2_D=OUT_OF_SCOPE_BY_USER
SCM_REGISTRATION=NOT_RUN_ADMIN_REQUIRED
SCM_START=NOT_RUN_ADMIN_REQUIRED
SCM_STOP=NOT_RUN_ADMIN_REQUIRED
SCM_RESTART=NOT_RUN_ADMIN_REQUIRED
SCM_UNREGISTER=NOT_RUN_ADMIN_REQUIRED
```

当前环境为 Windows，PowerShell 7.6.9，.NET SDK 10.0.402，当前会话不是管理员（`IsAdmin=false`）。服务 Release 构建成功，但真实 SCM 安装、启动、停止、异常恢复和升级保留数据不能在非管理员会话中安全执行。

本轮运行状态证据：`D:\study\NetConsole-Next-Workspace\reports\r3.2-blocker-runtime-20261115-004027\runtime-state.json`。其中确认 `NetConsoleNext` SCM 注册不存在；PID 43524 是另一个隔离 R7.8 候选实例，不属于本轮测试进程。

## 已验证

- `src/NetConsole.Next.Service/NetConsole.Next.Service.csproj` Release build：0 warning / 0 error。
- `src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj` Release build：0 warning / 0 error。
- `tests/service_installation_plan_tests.ps1`：27/27 PASS；无 SCM 或注册表写入。
- `tests/r73_scm_plan_tests.ps1`：5/5 PASS；明确标记 SCM lifecycle NOT_TESTED。
- `tests/NetConsole.Next.LocalManager.Tests`：Local Service Readiness 6/6、Portable Runtime Boundary 9/9、Local Deployment Doctor 14/14 PASS。
- `scripts/install-service.ps1` 的实际非管理员尝试在创建服务和数据目录前拒绝：`Run this script from an elevated PowerShell window.`，`SCM_INSTALL_DATA_ROOT_CREATED=False`，`SCM_SERVICE_PRESENT=False`。

## 未验证与阻断

完整的 `tests/windows_service_lifecycle.ps1` 需要管理员权限，会操作 Windows SCM、ProgramData、服务 ACL 和服务进程恢复，因此本轮没有绕过权限执行。由此以下项目保持未验证：

- `NetConsoleNext` 服务真实安装、自动启动、停止、重启和 failure recovery；
- LocalManager 对真实 SCM 的 start/stop/restart；
- 服务升级后数据库、Raw、token 和凭据数据保留；
- 真实 GUI 安装和 Windows 原生 125% DPI。

本轮没有创建 Windows 服务、没有写入注册表、没有修改 ProgramData、没有修改生产或真实开发数据根。应在明确的管理员维护窗口中单独执行 canonical lifecycle 脚本，并保留完整输出后再将上述状态改为 PASS。

