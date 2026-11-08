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
