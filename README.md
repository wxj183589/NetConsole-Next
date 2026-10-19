# NetConsole Next

**未来主力仓库；当前处于 P0/G1 稳定性原型阶段，不可作为生产采集软件。**

## 产品方向

- **P0 系统稳定性，P1 原始数据采集优先**；之后依次为工具集、实时分析、离线分析和报告。
- 主运行平台：Intel/AMD Windows x64 低配置工业 Mini 主机；Win8/8.1、Server2012/R2、Linux/银河麒麟、ARM64 均须实测兼容。
- Headless Core Service + Web UI；桌面仅保留未来的本机 Local Manager（服务启停、采集文件目录、日志）。
- 全新 SQLite + Raw 目录，绝不与旧 NetConsole 共用数据根，不兼容旧数据库。
- HTTP/FTP/TFTP/WebDAV/SSH/SFTP 服务均为未来按需启用组件，**默认不开启**；当前不建设 Agent Controller。
- 优先对比 C# 与精简 Python。当前 P0 使用 .NET 10 仅用于验证，不声明支持 Windows 8。

## 已实现范围

当前实现包括：SQLite Schema v1、数据目录安全标识、本机 loopback 健康 API、可选 UDP Syslog、有界接收队列、Raw NDJSON 二进制保真、受控结束时封段与 SQLite 索引。G1 基础增加了启动时 Raw/SQLite 对账、遗留 `.open` 恢复、有效 NDJSON 前缀校验与损坏尾部截断、未索引段补登、缺失文件标记、任务运行记录、跨进程单实例锁，以及进程资源和采集状态快照。

Windows Service 与本机 Local Manager CLI 已有基础代码：Service Control Manager 启停适配、安装脚本，以及 `status`、`start`、`stop`、`open-data` 命令。实际 Windows 安装、服务账户/权限和目标主机行为仍待验证。

**未实现：**Web 登录认证与业务界面、SSH/SNMP、文件服务、长周期运行和设备回归。不能宣称真实现场的数据完整性或 UDP 零丢包。

### .NET 10 原型本地运行

Linux/macOS:

~~~sh
export NETCONSOLE_NEXT_DATA_ROOT="$PWD/.local-data"
export NETCONSOLE_NEXT_SYSLOG_PORT=5514
dotnet run --project src/NetConsole.Next.Service
~~~

Windows PowerShell:

~~~powershell
$env:NETCONSOLE_NEXT_DATA_ROOT = "$PWD\.local-data"
$env:NETCONSOLE_NEXT_SYSLOG_PORT = "5514"
dotnet run --project src/NetConsole.Next.Service
~~~

健康端点：[http://127.0.0.1:18771/api/system/health](http://127.0.0.1:18771/api/system/health)。默认 Web 始终只绑定 localhost，默认 UDP 端口为 **0（关闭）**。对外 Syslog 必须明确设置 `NETCONSOLE_NEXT_SYSLOG_BIND`，且非 loopback 时还需 `NETCONSOLE_NEXT_ALLOW_REMOTE_SYSLOG=1`；正式现场必须先做好网络隔离。

数据目录必须为空，或已经存在合法的 `.netconsole-next-root` 标记。不允许指向旧版 `NetConsoleData` 生产目录。

### 构建与测试

~~~sh
dotnet build src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release
dotnet build src/NetConsole.Next.LocalManager/NetConsole.Next.LocalManager.csproj -c Release
python3 tests/smoke.py
python3 tests/g1_recovery.py
~~~

Linux smoke 验证健康端点、UDP 二进制保真、Raw 封段和 SQLite catalog。G1 集成测试模拟强制退出、`.open` 损坏尾部、未索引有效/损坏段、缺失文件、单实例和任务状态。Windows CI 编译 Service 与 Local Manager；Windows 服务真实安装/启动、旧系统兼容和现场长周期测试仍待目标环境验证。

### Windows Service 与 Local Manager 基础用法

在 Windows x64 上发布并安装 Service（安装需要提升权限）：

~~~powershell
dotnet publish src/NetConsole.Next.Service/NetConsole.Next.Service.csproj -c Release -r win-x64 --self-contained false
.\scripts\install-service.ps1 -ServiceExecutable .\src\NetConsole.Next.Service\bin\Release\net10.0\win-x64\publish\NetConsole.Next.Service.exe
~~~

Windows 默认新数据根为 `%ProgramData%\NetConsoleNext\data`；若目录非空且没有合法标记，Service 会拒绝启动。Local Manager 是本机命令行工具，不开放远程控制：

~~~powershell
dotnet run --project src/NetConsole.Next.LocalManager -- status
dotnet run --project src/NetConsole.Next.LocalManager -- start
dotnet run --project src/NetConsole.Next.LocalManager -- open-data
~~~

健康端点仅绑定 `127.0.0.1`。应用层计数不包含操作系统 UDP 缓冲区丢包。Raw 启动恢复只保留完整、可解析且以换行结束的记录；遇到第一个损坏记录时，会先将损坏尾部字节及其 SHA-256、原始段路径、偏移写入数据根下的 `recovery-evidence/`，验证证据后再截断原段并标记为 `INCOMPLETE`。证据文件不通过 Web/API 发布；恢复证据写入失败时保持源文件不变。合法但未索引的 `.ndjson` 会补登；SQLite 指向不存在文件时标记为 `MISSING`。自动恢复不会把不完整段伪装为完整段。

参见 [ADR](docs/ADR-0001.md)、[路线与门禁](docs/ROADMAP.md)。

现有正式产品：[NetConsole](https://github.com/wxj183589/NetConsole)。
