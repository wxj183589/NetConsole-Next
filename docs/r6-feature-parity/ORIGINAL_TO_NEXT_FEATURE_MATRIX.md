# 原版到 NetConsole-Next 映射

状态含义：`PASS_LOCAL` 仅表示当前代码和隔离测试证据；`PARTIAL` 表示存在可用闭环但仍有缺口；`BLOCKED_EXTERNAL` 表示需要凭据、设备或安装环境；`NOT_IMPLEMENTED` 表示尚无产品闭环。

| 原版模块 | Next 对应 | 完成程度 | 差距 | 后续任务 |
| --- | --- | --- | --- | --- |
| 设备管理/连接 | `DeviceManagement.cs`、`DevicesView.vue`、`H3cConnectionTestService.cs` | PARTIAL | 现场型号和完整资产批量闭环 | 设备资产工作台、现场回归 |
| AC/AP/Mesh | `WirelessReadModel.cs`、`WirelessView.vue`、无线 API | PARTIAL | 实时多 AC、双 AC、写入和 ownership 证据 | 无线运行态与模拟器/现场验收 |
| AP 身份 | `WirelessIdentityLifecycle.cs` | PASS_LOCAL_MUTATION | 当前隔离 JSON authority 尚未 schema 化 | schema-backed authority、merge/split |
| 配置备份 | `ConfigBackupService.cs`、`ConfigBackupsView.vue` | PASS_LOCAL | 真实型号兼容待验 | 现场只读采集 |
| 配置恢复 | `ConfigRestoreService.cs` | PARTIAL_SAFE_EXECUTE | 默认 writer 关闭，无真实回滚 | 仅在基线和凭据满足时实现 writer |
| 任务中心 | `BoundedCollectionScheduler.cs`、`TasksView.vue` | PASS_LOCAL | 现场重启/权限变化证据 | 统一任务详情和恢复验收 |
| SSH/Telnet/Linux/跳板 | `H3cSsh.cs`、`LinuxSshTerminalTransport.cs`、`TerminalSessionManager.cs` | PARTIAL | 目标机和真实中转未验 | 安全凭据下互操作测试 |
| 诊断/LLDP/拓扑 | `NetworkDiagnostics.cs`、`NetworkTraceroute.cs`、`H3cLldpTopology.cs` | PASS_LOCAL/PARTIAL | ARP、路由、WLAN/Mesh 诊断和资产视图不完整 | 诊断扩展、拓扑工作台 |
| 智能配置 | 无对应闭环 | NOT_IMPLEMENTED | 模板、变量、校验、执行边界缺失 | P1 设计与 dry-run |
| 文件/报表/导出 | `FileManagerService.cs`、前端 Files | PARTIAL | 原版报表和跨域导出仍需对齐 | 导出契约与报告任务 |
| 权限/审计 | `WebAuthStore.cs`、Site scope、Catalog audit | PARTIAL | 远程 TLS 和完整 mutation audit | 安全审计收口 |
| 部署升级 | LocalManager/Windows Service 基础 | BLOCKED_EXTERNAL | 安装、TLS、升级/回滚未验 | 离线候选与目标机门禁 |

