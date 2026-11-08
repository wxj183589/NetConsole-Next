# R7.8 原版能力迁移核对

日期：2026-11-09；分支 `codex-A/netconsole-next-r3-dual-ac`；已提交源码基线 `5500014`。整体 **PARTIAL**。本子任务只读 Next 源码、具名测试和既有报告，未重新访问原版源码或真实数据根，未运行测试套件、设备、Service 或 SCM，未提交和推送。主任务同期未提交的 UI 改动不作为本表已交付证据。

原版来源复用 [R7.1 只读盘点](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md) 和原矩阵所记 2026-10-19 映射。本次修正当前状态，不把原版成熟度继承为 Next 成熟度。汇总入口：[FEATURE_MIGRATION_MATRIX](../FEATURE_MIGRATION_MATRIX.md)。

状态：`EXISTING` 为复用既有 Next 能力；`IMPLEMENTED` 为具名有界能力具备源码/API/UI/测试覆盖；`PARTIAL` 为较大业务仅完成部分；`MISSING` 为未定位对应产品实现；`BLOCKED` 为外部能力或验收前置阻断。实现状态独立于实机验收。源码路径相对 `src/NetConsole.Next.Service/`；C# 测试相对 `tests/NetConsole.Next.Service.Tests/`；Vue 文件相对 `web/src/`。测试列只是核对入口，不表示本次重跑。

| 能力 / 状态 | 当前源码与入口 | 测试核对入口 | 边界 / 下一步 |
| --- | --- | --- | --- |
| 设备资产、Site、分组 / IMPLEMENTED | `DeviceManagement.cs`、`DeviceBatchOperations.cs`；`views/DevicesView.vue`；CRUD、精确详情、导入预览确认 | `DeviceConnectionTests.cs`、`TaskSiteScopeTests.cs`；`views/DevicesView.test.ts`、`DevicesConnectionTests.test.ts` | 具名资产能力存在；不据此判定现场全量对齐或全部原版编辑流程完成 |
| 网络 SSH/Telnet / PARTIAL | `TerminalTransports.cs`、`NetmikoRetryTransport.cs`、`NetmikoWorkerBudget.cs`、`tools/netmiko_worker.py` | `NetmikoWorkerBudgetTests.cs`、`NetmikoRetryTransportTests.cs`、`tests/netmiko_worker_test.py` | 复用官方 Netmiko、有界 worker/重试/Raw；跨型号与现场 Telnet 逐项验收，不自建协议 |
| Console、录制回放 / PARTIAL | `TerminalSessionManager.cs`、`TerminalRecordingServices.cs`、`TerminalExport.cs`；`views/TerminalView.vue` | `TerminalCoreTests.cs`、`InteractiveScopeTests.cs`、`tests/terminal_e2e.py`、`tests/web_terminal_browser_e2e.mjs` | 精确目标授权与录制存在；不能从 SSH fixture 推广全部现场 Telnet 或长会话 |
| Linux SSH / EXISTING | `LinuxServers.cs`、`LinuxServerEndpoints.cs`、`LinuxSshTerminalTransport.cs`；`views/LinuxServersView.vue` | `LinuxServerTests.cs`、`tests/linux_ssh_e2e.py`、`views/LinuxServersView.test.ts` | 独立 SSH.NET 服务器终端，不再用其描述网络设备传输；真实服务器互操作另验 |
| Site SSH 中转 / PARTIAL | `NetmikoJumpHostProbe.cs`、`Catalog.Terminal.cs`、`Program.cs` Site jump-host API、worker direct-tcpip | `H3cConnectionPolicyTests.cs`、Worker/终端/连接测试 | 配置、指纹、探测和 scoped 连接存在；无本轮真实跳板拓扑；不代理 ICMP/UDP/Syslog |
| H3C 身份、接口、VLAN / PARTIAL | `H3cComware.cs`、`H3cCapability.cs`、`H3cOperationalCapabilities.cs`、`H3cTaskFactPublication.cs`、`H3cVlans.cs`；H3C/设备详情/规划 UI | `H3cCapabilityTests.cs`、`H3cOperationalCapabilityTests.cs`、`H3cTaskFactPublicationTests.cs`、`H3cVlanTests.cs` | Raw/Facts/Current 有来源；缺失 UNKNOWN、过期 STALE；规划 VLAN 不等于设备 apply；完整编辑/性能趋势待迁移 |
| LLDP 与资产历史 / PARTIAL | `H3cLldpTopology.cs`、`H3cLldpListTable.cs`、`LldpFaultHistory.cs`；`views/TopologyView.vue` | `H3cLldpTopologyTests.cs`、`H3cTaskFactPublicationTests.cs`、`views/TopologyView.test.ts`、`tests/r78_topology_browser.mjs` | R7.8 有效变化历史、分页、并发上限/离页停止排队、图例伪链路过滤和管理地址验证；跨站/模糊关系仍 UNKNOWN |
| ZTE / PARTIAL | `ZteCollection.cs`、`Phase9VendorParsers.cs`；`views/ZteView.vue` | `ZteCollectionTests.cs`、ZTE fixtures、`views/ZteView.test.ts` | 已有采集/解析，不再标 NOT_STARTED；无本轮真实 ZTE 全链证据 |
| AC Domain、ONE_PLUS_ONE / PARTIAL | `WirelessReadModel.cs`、`WirelessRuntimeState.cs`、`WirelessCollectionManifest.cs`；`views/WirelessView.vue` | `WirelessTests.cs`、`WirelessRuntimeTests.cs`、`WirelessTaskTests.cs` | 复用冻结 R3；用户拓扑与设备角色分开。双 AC 只读或 R/M、R/B 不证明 HA/业务连续性 |
| AP Resource / Observation / 身份 / PARTIAL | `WirelessIdentityContract.cs`、`WirelessIdentityLifecycle.cs`、`WirelessProjectionPersistence.cs` | `WirelessIdentityContractTests.cs`、`WirelessIdentityLifecycleTests.cs`、`WirelessReplacementTests.cs` | Serial/MAC 稳定资源与来源观察分离；管理 IP 改变不创建新身份；完整生命周期和现场替换另验 |
| AP Group / PARTIAL | `WirelessGroupProjection.cs`、`WirelessReadModel.cs`；无线组/成员入口 | `WirelessGroupTests.cs`、`views/WirelessView.test.ts` | 同名不自动跨 AC 合并，保留 source/revision；完整权威组生命周期待齐 |
| FIT 原生 Telnet / BLOCKED | 已有 Netmiko Telnet 与 AC 托管 AP 路径 | Worker Telnet 回归、Wireless/Identity tests；[FIT 报告](R7_8_FIT_AP_REPORT.md) | 当前元数据显示 FIT 无存储 secret；不猜密。AC exec-console 命令接受不证明生效或 FIT 原生身份/交互 |
| SW 当前认证 / BLOCKED | 既有 SSH、接口/LLDP/VLAN/备份路径 | 历史采集报告及 [设备报告](R7_8_DEVICE_INTEROP_REPORT.md) | 存在历史成功与后续失败；无新有效凭据不重试，不把历史成功写为当前认证成功 |
| Mesh 分析 / PARTIAL | 已有 mesh 命令/raw、通用 parser 与有限无线投影 | H3C/Wireless golden fixtures | 未定位原版对等 Mesh 时序、质量/掉线分析、专项页面及 Excel；LLDP 不替代 Mesh |
| Online MR 会话 / MISSING | rail-reference/schema 是参考数据，未定位对等 MR 会话采集产品链 | `P2B5RailReferenceTests.cs` 只覆盖参考能力 | 通用 Raw/终端录制不是 MR 会话分析与专业报告；单独业务切片 |
| Syslog / PARTIAL | `UdpSyslogWorker.cs`、`SyslogQuery.cs`、`H3cSyslogEventClassifier.cs`；`components/SyslogWorkspace.vue` | `SyslogQueryTests.cs`、`tests/r71_feature_http_e2e.py`、`tests/r73_unattended_http.py`、Syslog Vue tests | 有界封存/查询/CSV/分类；不保证 UDP 零丢失，不从合成回环推定真实设备配置和长期负载已验 |
| ICMP/TCP、DNS、Traceroute / PARTIAL | `NetworkDiagnostics.cs`、`NetworkTraceroute.cs`；`views/ToolsView.vue` | `NetworkDiagnosticsTests.cs`、`NetworkTracerouteTests.cs`、`WindowsSystemTracerouteTests.cs`、`views/ToolsView.test.ts` | R7.8 保留实际 DNS 地址/IP_LITERAL 模式；AppHost error 5 独立 BLOCKED，DLL 成功不能代替 |
| Quick/Long Ping、趋势 / PARTIAL | `NetworkQuickPing.cs`、`LongPingControl.cs`、`PingLossEvidence.cs`；`components/QuickPingHistoryTrend.vue` | `NetworkQuickPingTests.cs`、`LongPingControlTests.cs`、`PingLossEvidenceTests.cs`、`tests/r76_long_ping_http_e2e.py` | 有界采样/协议/丢失率/RTT/P95/暂停续跑；原版 fping 长期车队、压缩/保留语义未全迁移 |
| TCP Traceroute / MISSING | 现有 TCP 是端口检查；当前路径是 ICMP/Windows tracert | `NetworkTracerouteTests.cs` | 不把 TCP 成功写成每跳 TTL 路径，需要独立支持与验证 |
| 吞吐/iperf / MISSING | 未定位产品/API/UI | 无对应 suite | TCP RTT 不能当吞吐，需要独立工具/进程/许可证与业务合同 |
| 脱敏备份与计划 / PARTIAL | `ConfigBackupService.cs`、`ConfigBackupScheduleService.cs`、`ConfigBackupBatchHistoryStore.cs`；`views/ConfigBackupsView.vue` | `ConfigBackupTests.cs`、`ConfigBackupScheduleTests.cs`、`views/ConfigBackupsView.test.ts` | RUNNING/SAVED/STARTUP_INFO/STARTUP_BODY 分开；单次/批量/历史/计划已存在，现场型号覆盖待齐 |
| 完整私有配置、比较、变化 / PARTIAL | `SimulatorConfigurationService.cs`、`CompleteConfigurationStorage.cs`、`CompleteConfigurationComparison.cs`、`CompleteConfigurationDrift.cs`、`CompleteConfigurationTargets.cs` | `SimulatorConfigurationTests.cs`、`CompleteConfigurationComparisonTests.cs`、`CompleteConfigurationBatchTests.cs`、`tests/r77_configuration_drift_http.py` | 原件与脱敏分离，精确身份/完整性/连续版本；R7.8 more 完整读取复用 Netmiko；不从 fixture 推定设备恢复 |
| 整机恢复/回滚 / BLOCKED | `ConfigRestoreService.cs`、`H3cCheckpointRestoreService.cs`、`ConfigurationRestoreReadiness.cs`；预览/门禁 | `ConfigRestoreTests.cs`、`H3cCheckpointRestoreTests.cs`、`ConfigurationRestoreReadinessTests.cs`、`tests/r72_restore_preflight_http.py` | 缺传输/设备文件hash/native replace/重连/回读/业务/回滚；区分 WRITER_ACCEPTED、CONFIG_APPLIED、CONFIG_READBACK_VERIFIED、BUSINESS_VERIFIED、ROLLBACK_VERIFIED |
| 智能配置 / PARTIAL | `SmartConfigurationService.cs`；`views/SmartConfigView.vue` 模板/预览 | `SmartConfigurationTests.cs`、`views/SmartConfigView.test.ts` | DRY_RUN_ONLY；无设备 apply、批量写入或回滚成功声明 |
| 故障中心/补采 / PARTIAL | `FaultCenterService.cs`、`FaultCenterRoutes.cs`、`OperationalHealthService.cs`；`components/FaultCenter.vue` | `FaultCenterTests.cs`、`OperationalHealthTests.cs`、`components/FaultCenter.test.ts`、`tests/r78_configuration_fault_http.py` | 持久 lifecycle/去重/历史/有界 policy 存在；采集失败不当 AP 离线，连接成功不清故障；真实模拟器故障恢复另验 |
| 活动 Long Ping 故障恢复 / IMPLEMENTED | 同资源协议/目标/端口窗口和 `ConfirmationStartSequence`，3 连续有效丢失/3 新回复 | `OperationalHealthTests.cs`、`FaultCenterTests.cs`、`tests/r78_live_ping_fault_http.py` | 拥有回环 TCP listener 的 HTTP 12 项通过；只证明这条有界链，不证明模拟器、无线业务或长期运行 |
| 配置变化自动事件 / IMPLEMENTED | 完整漂移接入 health/fault；有效稳定后续版本恢复 | Simulator/OperationalHealth/Fault tests、`tests/r78_configuration_fault_http.py`、`tests/r78_configuration_fault_browser.mjs` | 新鲜/连续/完整的备份才确认；UNKNOWN/损坏/未来/过期不确认；稳定不表示回滚 |
| 任务中心、批量/重试 / PARTIAL | `H3cBatchTaskService.cs`、`TaskPlanCatalog.cs`、`WirelessTaskHistory.cs`、`WirelessTaskService.cs`；`views/TasksView.vue` | `TaskPlanTests.cs`、`TaskSiteScopeTests.cs`、`TaskDiagnosticEvidenceTests.cs`、`WirelessTaskTests.cs` | 有界计划/取消/结果/Raw/重试；未覆盖全部原版类型、任意恢复和智能配置写入 |
| CSV/ZIP/文件/报表 / PARTIAL | `FileManagerService.cs`、`FileArchiveService.cs`、`TaskExportService.cs`、`P2B5ExportService.cs`、`TerminalExport.cs`；files/tasks/planning | `TaskExportTests.cs`、`P2B5SiteScopeTests.cs`、`tests/p2b5_export_e2e.py`、终端/诊断 HTTP tests | 具体格式/scope 下载存在；全部 Mesh/MR 专业 Excel、报表设计器和原版导入未迁齐 |
| 可选协议服务器 / MISSING | 服务内受控文件 API 不等于 HTTP/FTP/TFTP/WebDAV/SFTP Server | 无对应 suite | 默认不开启符合 AGENTS；出站依赖不是服务器，不自动扩大范围 |
| Windows/LocalManager / PARTIAL | LocalManager `LocalServiceReadiness.cs`、`LocalDeploymentDoctor.cs` 和 Windows 脚本 | `tests/NetConsole.Next.LocalManager.Tests/`、`tests/service_installation_plan_tests.ps1`、`tests/r73_scm_plan_tests.ps1` | R7.8 剩余截止时间与 PID 类型守卫；计划/离线通过不代替 SCM/账户/安装/升级/回滚/保留数据卸载 |
| SNMP v1/v2c / MISSING | 只有 future scheduler 注释，未定位 client/API/UI | 无对应 suite | 原矩阵实施计划不是现有能力证据 |

## 既有 R7.8 验证范围

以下核对既有文件，不表示本子任务重跑；最终候选以主任务实际 Gate 为准。

- [设备报告](R7_8_DEVICE_INTEROP_REPORT.md)：Worker 60 tests，覆盖官方 SSH/Telnet startup 正文内嵌提示符、缺终止/变更提示符/超时失败封闭；是离线传输测试。
- [故障报告](R7_8_FAULT_CENTER_REPORT.md)：候选 `r78-config-candidate` 完整配置 28/28、健康 26/26、故障 30/30；配置事件 HTTP 12 项、1920/2560 浏览器 15 项；合成配置不是恢复实机验收。
- Workspace `reports/r78-live-ping-fault-bbce467c2b13/result.json`：`SERVICE_E2E_ACTUAL_LOOPBACK_TCP`，12 项，运行中失联/恢复/复发、幂等、暂停不采样、续跑不用旧样本、取消历史保持 TCP；设备会话及配置写入 0。该 listener 不是模拟器/无线业务。
- [Windows 部署报告](R7_8_WINDOWS_DEPLOYMENT_REPORT.md)：LocalManager/SCM 计划门禁与普通 token AppHost error 5、显式 DLL 成功分列。报告 PID 仅属当次快照，不声明为当前运行实例。

## 后续迁移顺序

1. 先补备份提醒、ICMP 失败后显式 TCP 入口和精确设备/协议故障证据；主任务审查与测试正在开发的 UI，随后更新对应交付记录。
2. 有效授权 AC Profile 继续产品只读采集与 Raw 完整性；SW/FIT 新有效凭据到位后单次定向认证，不猜密、不扫描。
3. 恢复/HA 在完整备份、身份、受控触发/回滚、回读与业务证据齐备后验收；命令接受和角色字段不能代替结果。
4. Mesh/MR、SNMP、TCP TTL 路径、吞吐、专业报表按独立业务切片迁移，复用既有 worker/parser/service。
5. SCM 服务身份及 8–24h 实际长稳独立执行，不从 DLL 单次门禁或 300 秒观察推断。

本次只改两份文档，不改变权限、数据库、Raw、检查点、运行实例或静态资产；禁止远端 push/merge/release。

## 最终主集成补充（2026-11-09）

备份提醒76cda4f、显式TCP入口f7a1876、精确设备详情f444e42/ea85c54、诊断服务端分页2c21e66、总览未知a359197、拓扑部分失败4e1b09a均已本地提交并通过定向/全量及浏览器验证，不再是待开发项。最终Web450/Worker60/完整Service PASS，DLL完整离线ICMP失败。双AC新12 Raw仅只读，HA/恢复/SCM/长稳不升级为通过。以FINAL_ACCEPTANCE与TASKPOOL续跑。
