# 本机运行管理

在完整 Windows 发布目录运行 `NetConsole.Next.LocalManager.exe`，选择启动并打开、停止、重启、诊断或状态。静态 Web 由服务提供，无需 Node/Vite。关闭管理器不会停止采集。

已安装 `NetConsoleNext` Windows 服务时，简单入口使用 SCM，默认从服务自己的注册表环境读取数据根、端口和 EXE；权限不足会显示 Windows 错误，不创建第二个便携实例。服务安装、修改和卸载需要管理员；不能把便携进程视为 SCM 安装验收。

未安装 SCM 服务时，管理器启动自己的交互服务进程。默认程序为相邻的 `NetConsole.Next.Service.exe`，默认数据为当前用户 LocalAppData 下的 `NetConsoleNext\data`。可在程序旁保存 `local-manager.json`，或向命令传入配置文件的绝对路径：

```json
{
  "serviceExecutable": "D:\\NetConsoleNext\\v1\\NetConsole.Next.Service.exe",
  "dataRoot": "D:\\NetConsoleNextData",
  "webPort": 18771,
  "enableH3c": true,
  "enableTerminal": true
}
```

启用设备功能需要既有离线 Netmiko/Python bundle；使用现有环境变量配置时也沿用服务的能力检查。管理器不会连接设备或验证凭据。

自动化可调用 `portable-start`、`portable-stop`、`portable-restart`、`portable-status` 和 `diagnose`，后面可带配置路径。明确的 portable 命令只管理匹配 PID、启动时间、EXE 和数据根收据的子进程。优雅停止通过当前用户专用随机 named pipe 触发 Host shutdown，普通停止超时不会强杀进程。端口冲突不会停止占用者。状态/停止不会初始化不存在的数据根。

诊断复用只读 SQLite/instance/health/Raw/Netmiko 检查，并查看磁盘余量与滚动日志。低于 1 GiB 或 5% 可用容量显示 WARNING；服务日志策略为 14 文件、每文件 5 MiB。诊断最多检查 15 个日志，不删除数据，不递归扫描。

SCM 升级与回滚均使用 `scripts/update-service.ps1`，目标 EXE 必须在独立版本目录，不覆盖运行文件：

```powershell
.\scripts\update-service.ps1 -TargetExecutable D:\NetConsoleNext\v2\NetConsole.Next.Service.exe -DataRoot D:\NetConsoleNextData -PlanOnly
.\scripts\update-service.ps1 -TargetExecutable D:\NetConsoleNext\v2\NetConsole.Next.Service.exe -DataRoot D:\NetConsoleNextData
# 回滚使用同一入口，指定仍保留的旧版本 EXE。
```

脚本核对实际 SCM binary/data/port，保留服务环境与数据，停机、切换 binary、重启并检查相同 instance、SCM PID、SQLite 和 health。目标失败时尝试切回旧 binary 并独立检查健康；失败会明确报告。离线计划可额外指定 `-CurrentExecutable`，不要求修改 SCM。

这是二进制版本切换，要求两个版本的数据 schema 兼容。脚本不复制或降级数据库；有不兼容迁移的版本必须使用相应迁移恢复流程。本轮普通用户环境无法完成真实 SCM 安装/升级/回滚验收，须保持 NOT_TESTED。卸载继续使用既有 `uninstall-service.ps1`，保留数据与程序目录。

## ICMP 部署诊断

候选 Service 可以通过 `--icmp-diagnose` 诊断回环 ICMP 权限。该显式入口在 Host、互斥锁及数据根初始化之前运行，固定向 `127.0.0.1` 发送一次生产 Traceroute 所用的 TTL 1 探针，输出程序路径、运行时、架构和完整异常类型/native 错误码链。成功退出0，失败退出1。

```powershell
.\NetConsole.Next.Service.exe --icmp-diagnose
dotnet .\NetConsole.Next.Service.dll --icmp-diagnose
```

仓库 `tests/icmp_host_diagnostics.ps1` 可对同一 DLL 执行上述两种方式并写入对照报告，保留DLL哈希、当前管理员状态、EXE签名/MOTW/ACL和可读取的精确应用防火墙记录。任一方式失败时脚本退出1，原候选ICMP Gate仍需独立通过。

## 隔离 SCM 验收

`tests/r73_scm_lifecycle.ps1` 接受保留的当前版本、新版本、故意失败版本三个独立目录内的EXE、新的Next Workspace测试数据根、端口与报告路径。`-PlanOnly` 只核对路径和升级计划，输出计划。实际执行需要管理员令牌，遇到已有SCM服务或交互实例互斥锁会停止，保留现有运行环境。

实际脚本覆盖自动启动配置、虚拟账户、启动/重启、受控实例崩溃及SCM恢复、失败升级回退、升级、版本回滚、卸载保留数据。该脚本默认关闭设备功能，Worker生命周期和机器重启后的自动启动分别标为NOT_TESTED，需另用部署环境完成验收。三个版本必须数据schema兼容，测试数据与失败证据保留。
