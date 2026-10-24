# 里程碑与验收门禁

## P0 采集底座（已实现原型）
- 新仓库初始化、明确的隔离数据根、独立 Core 原型。
- SQLite v1 Schema、Raw 原始字节保真、UDP 本机接收、受限队列与计数。
- Web loopback 健康检查，不是可远程访问的业务 Web。
- 基本构建及端到端 smoke。未通过长期运行、断电和真实工业设备验证。

## G1 稳定性收口（当前优先）

- 已加入 schema v2 幂等升级、Raw 文件检查点和恢复中断标记。启动仍会枚举目录和核验路径/元数据；已登记且状态为 `FINALIZED`、大小与修改时间未变的文件不读取正文、不重写 Catalog。v1 文件首启仍完整验证；低优先级任务每次最多完整复检一个历史段。
- 未登记、`.open`、缺失、状态异常、检查点变化或校验不匹配文件仍会读取并校验。损坏尾部先持久化并校验证据，再修复有效前缀；完整校验差异写入 `raw_integrity_audits` 并将段标记为 `INCOMPLETE`。
- 健康状态区分 `HEALTHY`、`DEGRADED`、`FAULTED`。数据库/恢复根目录或 Raw 写入能力故障阻止新的采集；单个历史段问题可降级继续采集。
- 有界调度器可持久化任务 Definition/Run、排队/执行状态、超时、取消和失败原因。进程重启把排队/运行中任务标记为 `INTERRUPTED`，默认不自动重放。设备执行只使用模拟会话。
- Linux 上已有 SQLite busy/write fail、Raw 权限拒绝、采集中磁盘写失败、恢复中强杀、重复启动和 1,000 文件检查点用例。GitHub Windows Server 2025 工作流已实际通过 EXE 发布、虚拟账户/ProgramData ACL、Local Manager/SCM 生命周期、强杀恢复和卸载后保留数据。
- G1.5 加入 4 MiB 请求 UDP 接收缓冲（记录实际生效值）、8,192 条/32 MiB 双重有界队列、256 条/256 KiB/100 ms 自适应 Raw 批写；保留每批 `Flush(true)`、SQLite `synchronous=FULL` 与正常停止排空。
- Schema v3 记录 Raw 完整性问题处置。`QUARANTINED` 是保留原文件和哈希的逻辑处置；后续内容变化会新建审计并重新降级健康状态。
- 当前 Linux 容器提供了 100,000 条同速率前后对比、28 组 PPS/载荷追踪矩阵、30 秒稳态和 50,000 条/载荷突发诊断；详细结果和计数口径在 [PERFORMANCE_BASELINE.md](PERFORMANCE_BASELINE.md)。旧的 2,048 队列在同一 100k/5k PPS 测试中应用丢弃 11,460 条，8,192 队列版本收到并持久化 100,000 条。该容器测量不替代低配置主机验收。

## G2 可靠设备采集（基础组件进行中）

### G2.3 内置终端第一垂直切片

- 已在 G2.1 基线上加入可选 Terminal Session Manager：SSH.NET ShellStream、Telnet IAC/login、Windows COM 适配、服务器端 `RESTRICTED_READONLY` 输入策略、一次性 WebSocket ticket、有限输出队列和独立 JSONL 录制。
- Schema v6 保存会话元数据与 UTC 事件索引；录制文件保存带时间戳的输入/输出字节与 SHA-256。任意设备地址、未登记目标、原始字节输入和通用本机 Shell 均不开放。
- 当前切片不包含 xterm.js 设备管理 UI、会话检索/回放导出和物理串口验收；这些属于后续 PR B/C。SSH.NET 当前公开 API 只提供初始 PTY 尺寸，窗口变化请求仍需依赖后续库能力或受控适配；当前保留 gateway resize 契约但不宣称已完成远端 resize。

- G2.1 在 PR #10 的 SSH 只读适配器上增加 SSH.NET stdout/stderr 并行有界读取，修复非 seekable `PipeStream` 导致的输出限制失效；超限保留受限前缀并标为 `PARTIAL`，取消、超时、disconnect 与解析状态分开记录。Exec Channel 仍为当前实现，未在缺少目标设备证据时切换到交互 Shell。
- SQLite schema v5 将 Raw 段与 Task Run 关联，并在单个事务内写结果清单和任务终态。Raw 已封段但进程退出时，启动恢复校验内容并幂等重建清单；任务维持 `INTERRUPTED`，不会被恢复成成功。
- Windows 凭据采用 DPAPI `LocalMachine` 加密文件，Local Manager 提供 set/update/revoke；数据根 ACL 仅授予 SYSTEM、Administrators 和服务虚拟账户。Linux/Kylin 使用显式不可用 provider，环境变量 provider 仅在 Development 且显式开启时生效。
- 本机所有 `/api/system/*` 管理端点保留 bearer token、来源和 Host 检查；Web PR-1 增加独立 Cookie 会话、PBKDF2 用户、角色检查、CSRF/Origin 校验和显式 LAN bind，同时保持 loopback 默认。详见 `WEB_AUTH_AND_LAN.md`。
- 已加入 host-key 变化/缺失、认证失败、连接取消/超时、设备断连、输出边界、分页提示、未知格式、事务回滚与恢复幂等测试。另用 Paramiko 本机 SSH server 与 SSH.NET 实际 wire 通道覆盖 host-key、认证、Exec、静默握手取消、连接重置和双并发会话，并以 fake transport 覆盖清理边界。模拟不等于 Comware 固件互操作；未连接真实设备，标记 `PENDING_REAL_ENVIRONMENT`。
- G2.1 Linux 与 Windows Server 2025 Release 构建、C# 测试、Paramiko/SSH.NET wire 模拟及 Windows SCM/DPAPI/ACL 工作流通过（PR #11 head `426a42c2fd8b3f91576444ec2bd9325052b7bdb5`，Actions run `39638777555`）。Windows CI 曾发现旧 Raw 路径反斜杠与恢复正斜杠产生重复 Catalog 行；现已修复为写入规范化路径、恢复时按 Windows 物理路径识别并保留现有 DB 路径键，并加入 Windows 回归测试。PR #10 仍是未合并依赖，PR #11 保持独立且未合并。
- H3C stdout 是 **SSH.NET 解码后的 UTF-8 命令输出**，不是 SSH 线路字节。Raw capture、`PARSED / EMPTY / UNRECOGNIZED / PARTIAL` 解析状态与任务终态是三个不同事实。最大输出 256 KiB；命令分页时失败，不自动降低 SSH 算法要求。
- 正式现场 SSH/SNMP 采集、Syslog 多来源和 MR 实时采集仍须通过 G1 数据安全门禁、凭据管理评审和目标设备验证。
- 持久调度仍为本机有界队列；没有分布式调度、自动重试、远程控制或业务任务 Web 页面。
- Linux 合成设备并发 2/4/8/16 配合 128 个低速 Syslog 报文，4 档均观测到 128 收到、入队及 Raw 持久化、0 应用丢弃；结果是功能/资源样本，不是 SSH 设备吞吐或工业主机性能门禁。详见 [性能基线](PERFORMANCE_BASELINE.md)。
- 在 2核4GB 与 4核8GB 目标主机执行 72 小时和 7 天测试，记录 CPU、工作集、IO、丢包量和数据缺口。
- Windows Server 2025 GitHub runner 的当前分支 CI 已验证；Windows 10/11、8/8.1、Server 2012/R2 与低配置设备仍按 [兼容矩阵](COMPATIBILITY_MATRIX.md) 标记为待目标环境验证，不能从 Server 2025 CI 推导其兼容性。

### G1/G2 尚未关闭的验收项
- **待真实环境：**Windows 10/11 Service 安装/权限/升级；获现场授权的 H3C SSH Host Key、认证、Exec Channel、分页、版本、接口输出和算法互操作；低配置工业主机 72 小时/7 天负载、物理磁盘耗尽/断电。Server 2025 CI 与 Linux 故障注入不替代目标机验证。
- **协议边界：**没有隔离 Comware 设备时，SSH 认证/host-key/exec/reset 由可注入 transport 模拟覆盖；SSH.NET 的真实握手和 CLI 模式属于 `PENDING_REAL_ENVIRONMENT`。
- .NET 10 不支持 Windows 8/8.1 目标。SSH.NET 包含较旧目标框架资产不等于整个 ASP.NET Core/Service 可在旧 Windows 运行；旧系统需单独评估独立兼容宿主，不得降级当前密码学要求。

## P2-P5
- Web PR-2: host-tool registry/Desktop Helper boundary, logical-root file manager, owner-bound ZIP jobs and elevated Web password recovery are implemented in the PR-2 branch. Real Windows interactive GUI and LAN acceptance remain separate gates.
- P2：按成熟开源模式重新设计工具集和可选 HTTP/FTP/TFTP/WebDAV/SFTP Server。
- P3：实时轻量监测、RSSI/MESH 概览。
- P4：离线 MR/MESH 深度分析。
- P5：报告导出及轨道交通完整功能逐步接管。

旧 NetConsole 仍是生产软件；新版所有关键能力验收完成后才允许切换主线。
