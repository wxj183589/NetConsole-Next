# Windows Service 安装与运行

R7.1 的 Service 构建/离线候选会附带本目录的安装、只读计划及卸载脚本。它们不自动执行。目标机需要 Windows x64 .NET 10 ASP.NET Core Runtime；启用设备采集时需要与当前 Worker 匹配的完整离线 Netmiko 包。

## 安装计划

在候选根目录使用 PowerShell，先生成不写目录、注册表或 SCM 的计划：

```powershell
.\deployment\install-service.ps1 -ServiceExecutable .\NetConsole.Next.Service.exe `
  -DataRoot D:\NetConsoleNextData -WebPort 18771 -SyslogPort 0 -EnableH3c -PlanOnly
```

Netmiko 默认路径为候选内 `tools\netmiko\runtime\python.exe` 和 `tools\netmiko\netmiko_worker.py`。普通 `dotnet publish` 只有 Worker 源码，不含 Python；这种输出不能直接启用 H3C。请使用现有 `tools/publish_windows_offline.py` 组合经过验证的完整依赖包，或提供有效的绝对 `-NetmikoPython`、`-NetmikoWorker`。目标服务账户也必须能读取外部路径。

## 安装与启动

在管理员 PowerShell 中运行同一命令并去掉 `-PlanOnly`。安装写入该服务专属的 `Environment` REG_MULTI_SZ，包含数据根、端口及 Worker 路径；无需设置机器全局环境变量。服务使用 `NT SERVICE\NetConsoleNext`，设为自动启动，异常退出恢复延迟为 5/15/60 秒。安装不立即启动服务。

```powershell
Start-Service NetConsoleNext
Get-Service NetConsoleNext
Invoke-RestMethod http://127.0.0.1:18771/api/system/health
```

Web 仅绑定本机回环。Syslog 默认关闭；显式非零端口也只接收回环 UDP。远程 Syslog 的防火墙、绑定及允许列表不由安装脚本自动开放。首次运行使用应用的登录初始化方式；已有凭据不会被安装器重置。

`DataRoot` 必须独立于程序目录；拒绝卷根、旧 NetConsole 数据根、reparse 路径和非空未标记目录。首次启动创建 Next 根标记和本机存储。日志位于该根下 `logs`，保留既有滚动策略；Windows Application EventLog 使用 `NetConsoleNext` 来源。磁盘不可写或完整性失败时，查看 health 与日志，保留原数据诊断。

## 日常维护

停止/重启使用 `Stop-Service` / `Restart-Service` 或单独发布的 LocalManager。正常停止给后台任务、Raw 和自有 Worker 45 秒退出窗口。需要 doctor 时运行 `NetConsole.Next.LocalManager.exe doctor <DataRoot> <WebPort>`；它只读目录/数据库并核对实例和 Worker，不连接设备。

升级前正常停止服务，保留数据根，仅替换经过验证的程序与匹配依赖；本轮未实现自动升级/回滚安装器。卸载使用同目录 `uninstall-service.ps1`；其删除服务注册，保留数据根。请勿将原版 NetConsole 数据目录作为 Next 根。

本机非管理员运行仅完成只读安装计划、构建随包脚本、doctor 和正常进程启停验证；SCM 安装、开机自启、服务账户 ACL、崩溃自动恢复及目标机升级/卸载仍需独立实际验收。
