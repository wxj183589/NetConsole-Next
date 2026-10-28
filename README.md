# NetConsole Next

**未来主力仓库；当前处于 P0/G1.5 稳定性与 G2 基础采集开发阶段，不可作为生产采集软件。**

## 产品方向

- **P0 系统稳定性，P1 原始数据采集优先**；之后依次为工具集、实时分析、离线分析和报告。
- 主运行平台：Intel/AMD Windows x64 低配置工业 Mini 主机；Win8/8.1、Server2012/R2、Linux/银河麒麟、ARM64 均须实测兼容。
- Headless Core Service + Web UI；桌面仅保留本机 Local Manager 与可选 Desktop Helper（服务启停、受控文件目录、宿主机 GUI 协作）。
- 全新 SQLite + Raw 目录，绝不与旧 NetConsole 共用数据根，不兼容旧数据库。
- HTTP/FTP/TFTP/WebDAV/SSH/SFTP 服务均为未来按需启用组件，**默认不开启**；当前不建设 Agent Controller。
- 优先对比 C# 与精简 Python。当前 P0 使用 .NET 10 仅用于验证，不声明支持 Windows 8。

## 已实现范围

当前实现包括：SQLite Schema v1、数据目录安全标识、本机 loopback 健康 API、可选 UDP Syslog、有界接收队列、Raw NDJSON 二进制保真、受控结束时封段与 SQLite 索引。G1 基础增加了启动时 Raw/SQLite 对账、遗留 `.open` 恢复、有效 NDJSON 前缀校验与损坏尾部截断、未索引段补登、缺失文件标记、任务运行记录、跨进程单实例锁，以及进程资源和采集状态快照。

设备采集框架提供有界调度队列、最大并发限制、持久 Task Definition/Run、超时、取消和失败记录。G2.1 加固 H3C Comware SSH 只读组件，仅允许 `display version` 和 `display interface brief`，使用 SHA-256 Host Key pin、算法 allowlist、受限输出读取、Paramiko 本机 SSH wire 模拟及脱敏 golden tests；未连接真实设备。SSH 功能默认关闭，需显式设置 `NETCONSOLE_NEXT_H3C_ENABLED=1`。Windows 正式凭据通过 Local Manager 写入 DPAPI `LocalMachine` 加密 vault，数据库只存引用；Linux/Kylin 没有 DPAPI provider。环境变量凭据仅在显式 Development 模式并设置 `NETCONSOLE_NEXT_H3C_DEV_ENV_CREDENTIALS=1` 时启用。

Windows Service 与本机 Local Manager CLI 有基础实现：安装时使用专用虚拟服务账户，数据根 ACL 仅授予 SYSTEM、Administrators 与服务身份；Local Manager 提供 Service 控制、目录打开、`api-token`、`rotate-api-token`、`credential set`、`credential revoke` 与 `web-password reset`。Web PR-2 增加了 SUPERADMIN-only 宿主机工具注册、Named Pipe Desktop Helper、逻辑根文件管理、后台 ZIP 与本机密码恢复；`/api/system/*` 管理端点要求来源/Host、Cookie 会话或本机 bearer token，Cookie 写入要求 CSRF。真实 Windows 桌面程序启动仍待目标主机验收。

**待验收或后续：**SNMP、Syslog 多来源、MR 采集、真实 SecureCRT/Xshell/PuTTY/Explorer 启动、真实 H3C 设备/Comware Exec 兼容、设备并发性能和长周期运行。Web PR-2 文件管理是受控本机逻辑根访问，不是通用文件服务。DPAPI 是本机 Windows vault，不是远程凭据服务。不能宣称真实现场的数据完整性、UDP 零丢包或工业机性能达标。

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
dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release
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

卸载仅移除 Windows 服务登记，不删除发布文件或数据目录：

~~~powershell
.\scripts\uninstall-service.ps1
~~~

Windows 默认新数据根为 `%ProgramData%\NetConsoleNext\data`；若目录非空且没有合法标记，Service 会拒绝启动。Local Manager 是本机命令行工具，不开放远程控制：

~~~powershell
dotnet run --project src/NetConsole.Next.LocalManager -- status
dotnet run --project src/NetConsole.Next.LocalManager -- start
dotnet run --project src/NetConsole.Next.LocalManager -- restart
dotnet run --project src/NetConsole.Next.LocalManager -- open-data
dotnet run --project src/NetConsole.Next.LocalManager -- open-raw
dotnet run --project src/NetConsole.Next.LocalManager -- open-logs
~~~

健康端点仅绑定 `127.0.0.1`。应用层计数不包含操作系统 UDP 缓冲区丢包。Raw 启动恢复只保留完整、可解析且以换行结束的记录；遇到第一个损坏记录时，会先将损坏尾部字节及其 SHA-256、原始段路径、偏移写入数据根下的 `recovery-evidence/`，验证证据后再截断原段并标记为 `INCOMPLETE`。证据文件不通过 Web/API 发布；恢复证据写入失败时保持源文件不变。合法但未索引的 `.ndjson` 会补登；SQLite 指向不存在文件时标记为 `MISSING`。自动恢复不会把不完整段伪装为完整段。

本机管理 API 首次初始化时生成高熵 token，保存在隔离数据根的 `local-api-token` 文件。Linux 权限为 `0600`；Windows 从 ProgramData 受限 ACL 继承。通过 Local Manager `api-token` 查看，`rotate-api-token` 立即使旧 token 失效。远程 Web 管理尚未实现；将来需增加用户认证、明确角色授权、来源验证、CSRF 防护和审计。

参见 [ADR](docs/ADR-0001.md)、[路线与门禁](docs/ROADMAP.md)。

实现与验收记录见 [当前架构](docs/ARCHITECTURE.md)、[数据库设计](docs/DATABASE_DESIGN.md)、[功能迁移矩阵](docs/FEATURE_MIGRATION_MATRIX.md)、[兼容性矩阵](docs/COMPATIBILITY_MATRIX.md)、[性能基线](docs/PERFORMANCE_BASELINE.md)、[测试证据](docs/TEST_EVIDENCE.md) 与 [开源引用和依赖登记](docs/OPEN_SOURCE_REFERENCES.md)。

现有正式产品：[NetConsole](https://github.com/wxj183589/NetConsole)。
