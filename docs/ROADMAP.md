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

## G2 可靠设备采集（基础实现待独立 PR）

- 下一步用独立 PR 实现 H3C Comware SSH 最小只读采集、脱敏回显 golden tests、超时/取消/Host Key 验证和任务结果 Raw 指针；模拟设备完成测试，未授权前不连接现场设备。
- 正式 SSH/SNMP 采集、Syslog 多来源和 MR 实时采集仍须通过 G1 数据安全门禁和目标设备验证。
- 持久调度目前是内部模型/调度核心，没有设备驱动、自动重试、远程控制或业务任务 Web 页面。
- 在 2核4GB 与 4核8GB 目标主机执行 72 小时和 7 天测试，记录 CPU、工作集、IO、丢包量和数据缺口。
- Windows 10/11、8/8.1、Server2012/R2 单独建立兼容矩阵；非 Win10 系统不能以构建成功代替验收。

### G1 尚未关闭的验收项
- **待真实环境：**Windows 10/11 Service 安装/权限/升级，低配置工业主机 72 小时/7 天负载、物理磁盘耗尽/断电。Windows Server 2012/R2 当前已无 ESU；Windows 8/8.1 没有受支持的现代 .NET 10 路径。Server 2025 CI 与 Linux 故障注入不替代目标机验证。

## P2-P5
- P2：按成熟开源模式重新设计工具集和可选 HTTP/FTP/TFTP/WebDAV/SFTP Server。
- P3：实时轻量监测、RSSI/MESH 概览。
- P4：离线 MR/MESH 深度分析。
- P5：报告导出及轨道交通完整功能逐步接管。

旧 NetConsole 仍是生产软件；新版所有关键能力验收完成后才允许切换主线。
