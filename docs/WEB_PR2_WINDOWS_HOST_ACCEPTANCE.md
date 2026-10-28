# Web PR-2 Windows 宿主机验收

这份清单用于已授权的 NetConsole Next Windows 宿主机。它不安装第三方软件、不修改正式服务配置、不启动 Windows Service、不自动连接真实设备，也不读取或输出密码/API Token。

## 1. 准备

1. 使用管理员 PowerShell 登录目标 Windows 交互桌面。
2. 确认服务已由正式安装流程部署，Desktop Helper 由同一已登录用户在交互 Session 启动。Windows Service 不得在 Session 0 直接启动 GUI。
3. 创建专用隔离数据目录，例如 `D:\NetConsoleNext-acceptance-data`，不要指向正式 Raw、SQLite、录制或凭据目录。
4. 确认目标主机没有使用生产设备凭据。外部工具测试只使用合成设备档案或不带连接参数的进程启动。

## 2. 只读检查

```powershell
.\tools\acceptance\Test-WebPR2-Host.ps1 `
  -Mode CHECK_ONLY `
  -BaseUrl http://127.0.0.1:18771 `
  -ReportPath .\web-pr2-host-check.json
```

脚本输出 JSON。它检查管理员权限、.NET runtime、`NetConsoleNext` Service、Desktop Helper 进程、`NetConsoleNext.DesktopHelper` Named Pipe、可读的 Pipe ACL、Web health、监听端口和 SecureCRT/Xshell/PuTTY/Explorer 安装状态。`CHECK_ONLY` 不启动进程、不启动服务、不改配置。

`PIPE_ACL` 只有在能读取 Named Pipe ACL 且没有 Everyone/Users/Authenticated Users 等宽泛 ACE 时才算通过。不能通过放宽 ACL 到 Everyone 修复问题。

## 3. Desktop Helper 验收

```powershell
.\tools\acceptance\Test-WebPR2-Host.ps1 -Mode DESKTOP_HELPER_TEST -ReportPath .\desktop-helper-check.json
```

随后由操作员在 Web 中以 `superadmin` 执行一次“打开宿主机目录”和一次外部工具请求，并记录：

- Service 能连接本机 Named Pipe；
- Helper 窗口出现在当前交互桌面，而不是 Session 0；
- 注销用户、停止 Helper、重新登录后分别得到 `HOST_DESKTOP_UNAVAILABLE` 或受控失败，并能在 Helper 恢复后重新请求；
- 普通 `admin` 得到 403，不能配置或启动外部工具；
- 多用户/RDP 场景明确指定目标交互 Session，不接受“碰巧打开”的结果。

不得把 GitHub Windows Runner 或 Mock 程序作为真实 GUI 证据。

## 4. 外部程序验收

先执行只读安装检查。未安装的软件记录为 `NOT_INSTALLED`，不得用 `cmd.exe`、Mock 或其他程序冒充：

```powershell
.\tools\acceptance\Test-WebPR2-Host.ps1 -Mode EXTERNAL_TOOL_TEST -ReportPath .\external-tools.json
```

如需启动真实 GUI，必须由操作员显式确认。脚本只对 SecureCRT、Xshell、PuTTY 做无连接参数的进程启动；Explorer 只能打开 `-DataRoot` 指定的隔离目录：

```powershell
.\tools\acceptance\Test-WebPR2-Host.ps1 `
  -Mode EXTERNAL_TOOL_TEST -Tool Explorer `
  -DataRoot D:\NetConsoleNext-acceptance-data `
  -AllowGuiLaunch -ConfirmOperator `
  -ReportPath .\explorer-launch.json
```

结果必须分开记录：

```text
PROCESS_LAUNCH_VERIFIED
GUI_VISIBLE_VERIFIED
CONNECTION_ATTEMPT_VERIFIED
REAL_DEVICE_LOGIN_VERIFIED
```

本轮不要求真实设备登录；密码不得出现在进程参数。SecureCRT/Xshell/PuTTY 的设备 IP、端口、用户名参数和 Web SUPERADMIN 策略由操作员使用合成设备档案逐项核验。

## 5. 文件和 ZIP 验收

```powershell
.\tools\acceptance\Test-WebPR2-Host.ps1 `
  -Mode FILE_MANAGER_TEST -DataRoot D:\NetConsoleNext-acceptance-data
.\tools\acceptance\Test-WebPR2-Host.ps1 `
  -Mode ARCHIVE_TEST -DataRoot D:\NetConsoleNext-acceptance-data
```

脚本只确认隔离根存在，不伪造 API 成功结果。自动化文件/归档测试应运行仓库中的服务测试和 `tests/web_pr2_e2e.py`，覆盖逻辑根、分页、中文名、Range、ZIP CRC、取消、队列/用户配额、路径穿越、Reparse Point、SQLite/凭据/`.open` 保护，以及刷新后任务状态。正式 Raw 数据不得用于压力测试。

## 6. LAN、密码重置和证据边界

- 默认 Web 绑定必须是 Loopback；LAN 绑定只能由显式配置开启。
- 本轮不通过关闭 TLS 校验、暴露明文管理员登录或放宽 Origin/CSRF 来“通过”跨主机验收。PR #17 的上游 LAN/TLS 风险仍由 PR #17 处理。
- 密码重置使用管理员权限运行 `tools\Reset-WebPassword.ps1` 或 `tools\重置Web密码.cmd`，并指定隔离数据根；不直接编辑数据库，不把密码放入日志或命令行。
- 真实 Windows GUI、SecureCRT、Xshell、PuTTY、Explorer 和 LAN 跨主机结果必须附操作员截图/日志；云端 CI 只能证明构建、SCM 和自动化测试。
