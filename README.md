# NetConsole Next

**未来主力仓库；当前处于 P0 技术原型阶段，不可作为生产采集软件。**

## 产品方向

- **P0 系统稳定性，P1 原始数据采集优先**；之后依次为工具集、实时分析、离线分析和报告。
- 主运行平台：Intel/AMD Windows x64 低配置工业 Mini 主机；Win8/8.1、Server2012/R2、Linux/银河麒麟、ARM64 均须实测兼容。
- Headless Core Service + Web UI；桌面仅保留未来的本机 Local Manager（服务启停、采集文件目录、日志）。
- 全新 SQLite + Raw 目录，绝不与旧 NetConsole 共用数据根，不兼容旧数据库。
- HTTP/FTP/TFTP/WebDAV/SSH/SFTP 服务均为未来按需启用组件，**默认不开启**；当前不建设 Agent Controller。
- 优先对比 C# 与精简 Python。当前 P0 使用 .NET 10 仅用于验证，不声明支持 Windows 8。

## 已实现的 P0 范围

当前分支的代码仅验证：SQLite Schema v1 初始化、数据目录安全标识、本机 loopback 健康 API、可选本机 UDP Syslog 接收、有界接收队列、Raw NDJSON 精确保留原始字节、正常结束时封段与 SQLite 索引。

**未实现：**正式 Windows Service / Local Manager、Web 登录认证与业务界面、SSH/SNMP、任务调度与进程恢复、文件服务、段崩溃恢复/回读对账。未进行长期工业主机测试，不能宣称真实环境的数据完整性。

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
python3 tests/smoke.py
~~~

Linux smoke 检查健康端点、UDP 原始字节、Raw 封段和 SQLite catalog；Windows 当前只有 CI 编译检查。原始 `.open` 文件在异常退出时保留，但自动恢复和 catalog 对账属于后续 G1，**尚未通过验收**。

参见 [ADR](docs/ADR-0001.md)、[路线与门禁](docs/ROADMAP.md)。

现有正式产品：[NetConsole](https://github.com/wxj183589/NetConsole)。
