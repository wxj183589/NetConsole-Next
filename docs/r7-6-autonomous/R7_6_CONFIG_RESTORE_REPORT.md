# R7.6 配置备份与恢复事实报告

日期：2026-11-08。Agent：codex-A。报告基线：`39c2827`。状态：**PARTIAL / FULL_RESTORE_BLOCKED**。

本轮完成备份读写路径的祖先重解析点保护，并收紧检查点恢复的 LLDP 业务验证。定向自动化门禁通过；本轮没有执行设备配置写入、整机替换或真实失败回滚。最终候选的配置子门禁通过，但两种启动方式的完整离线门禁均因必需 Traceroute 失败而 EXIT 1；配置 UI 最终浏览器验收尚无完成证据。

## 本轮修复及验证

| 项目 | 修改与已验证结果 | 主线提交 |
| --- | --- | --- |
| 备份写入路径 | 写入前检查现有路径及祖先的 ReparsePoint，阻断经 Junction/符号链接写出受控备份目录；复用既有备份 Service | `3f65378` |
| 备份读取路径 | 历史枚举跳过重解析目录；列表、单项读取、下载、差异均检查祖先链接，避免只检查最终文件而穿过已替换目录 | `a36f23a` |
| LLDP 恢复业务证据 | 复用严格 `ParseLldpListTable(completedExec:true)`；权限拒绝、分页、坏行、边界不完整及混入错误的零邻居文本不能作为正常空集 | `bf245ad` |

祖先 Junction 回归先在旧实现失败，加入保护后通过。LLDP 回归同样先证明旧实现可误收权限拒绝文本，再验证修复。明确且完整的零邻居响应仍被接受；正常表格空结果按既有严格解析器语义处理。

检查点恢复新增四组覆盖：无效 LLDP 基线在 commit/描述写入前阻断、明确空邻居接受、恢复后邻居漂移、恢复后 LLDP 读取被拒绝。即使配置哈希相同，业务探针漂移或不可读取也不能设置 `SERVICE_VERIFIED` 或 `ROLLBACK_VERIFIED`。业务证据范围仅为接口 Link/Protocol 与 LLDP 邻居，不包括终端流量、无线 HA 或跨设备端到端业务。

| 定向检查 | 本轮已执行结果 | 证据边界 |
| --- | --- | --- |
| Configuration Backup | 12/12 PASS | 最小隔离 fixture，包含读写路径和 Junction 回归 |
| Configuration Backup Schedule | 19/19 PASS | 合成调度与持久化门禁 |
| Restore readiness | 16/16 PASS | 恢复前只读检查与历史状态 |
| H3C checkpoint restore | 18/18 PASS | 注入 session，包含严格 LLDP 新回归 |
| Simulator configuration | 22/22 PASS | 合成 session，不能代替真实设备恢复 |
| Restore preview/token | PASS | 既有预览与确认令牌回归 |
| Service / 测试 Release build | 0 warnings / 0 errors | 对应修复 worktree 的定向构建 |
| `git diff --check` | PASS | 对应代码修复与本报告检查 |

这些结果来自本工作流此前实际执行的定向检查。本报告编写阶段只读复核源码、提交和既有证据，没有重复运行服务或设备命令。

## 历史设备写入与恢复证据

已有证据 `D:\study\NetConsole-Next-Workspace\reports\r69-config-writer-20261107-225623\summary.json` 标记为 **SIMULATOR**。其真实 SSH 目标为 SW `192.168.56.3:22`，S6850 / Comware `7.1.070, Alpha 7170`；仅在 DOWN、无 LLDP 邻居且无业务配置的 `GigabitEthernet1/0/48` 上验证可逆描述 Writer。

| 已有步骤 | 报告记录 |
| --- | --- |
| 完整基线 | `02e229f51a774c2b970bc51d42d5e97e`，6574 原始字节 |
| 写入临时描述 | operation `933ae02637ae405aafa97fc0a495c149`，`VERIFIED` |
| 恢复原无描述状态 | operation `73e38ff45d484ba69a23f5803e725115`，`VERIFIED` |
| 独立最终采集 | `d593b40ea2b14b04be2673fcfd0eb0aa`，6574 原始字节 |
| 前后原始配置 SHA256 | `5c38013d4c0408bbe9a7104d8dd3eefbf75ac45f47590d71ec6d1cdd4550cbb0` |
| 前后 canonical SHA256 | `0c1f167f14300ea6ad2262e23596404b0fa905fc1f88ecc853be2e2f28171161` |

这是历史受控模拟器描述变更与恢复证据，本轮只读复核了原报告。它不构成独立物理硬件验收，也不构成整机历史配置替换或故意故障触发自动回滚。该报告两个成功操作的 `rollbackBackupId` 均为 null；合成 session 的 `ROLLBACK_VERIFIED` / `ROLLBACK_FAILED` 覆盖应独立记录。

## 整机恢复仍有硬阻断

既有 R7.0 真实受控验证报告记录 `configuration commit` 后仍无检查点，HTTP 操作为 `NATIVE_CHECKPOINT_NOT_CREATED`。R7.2 原始预检报告 `reports/r72-restore-preflight-20261108-034110.json` 记录完整原件与两次独立读取稳定、接口/LLDP 基线稳定，但返回 `READ_ONLY_PREFLIGHT_COMPLETE_RESTORE_BLOCKED`。

其阻断为 `SFTP_SUBSYSTEM_UNAVAILABLE`、`DEVICE_FILE_UPLOAD_AND_HASH_UNVERIFIED`、`NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED` 和 `POST_RESTORE_AND_ROLLBACK_UNVERIFIED`。原生帮助广告 replace 语法不能代替 staging、设备端长度/哈希、确认交互和实际恢复证据。当前真实 session 的原生检查点替换能力仍受保护。

| 验收项 | R7.6 结论 |
| --- | --- |
| 独立物理硬件 Writer | NOT_TESTED；仅保留上述历史 SIMULATOR 描述 Writer 证据 |
| 整机历史文件上传 / native replace | BLOCKED / NOT_TESTED |
| 整机恢复后独立重连与完整配置哈希 | NOT_TESTED；恢复前稳定读回不算恢复后证据 |
| 整机恢复后业务探针 | NOT_TESTED；严格解析器回归为合成证据 |
| 故意失败与自动整机 rollback | NOT_TESTED；合成回滚状态覆盖已通过 |
| 最终 R7.6 候选离线配置门禁 | CONFIGURATION_KINDS / BACKUP_SCHEDULES 子项两模式均 PASS；完整离线 APPHOST / DOTNET_DLL_EXPLICIT 均 EXIT 1，阻断为必需 Traceroute 的 ICMP_PERMISSION_DENIED |
| 最终 R7.6 候选配置 UI 浏览器门禁 | NOT_VERIFIED；尚未取得最终候选在 1920×1080 / 2560×1440 的实际浏览器操作与截图证据 |

最终候选为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`，Service.exe ProductVersion 为 `1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。`reports/r76-offline-apphost.log` 与 `reports/r76-offline-dotnet.log` 记录 running/saved/startup、独立版本、脱敏下载、缺失 saved 失败、计划 CRUD/角色/输入、持久重启、逾期只执行一次、SAVED 和损坏存储隔离子项通过。这些使用回环 SSH 合成设备，不能替代真实设备恢复。全量 Service 日志 `reports/r76-service-full-final.log` 为基线 `50db848` EXIT 0，包含 Backup 12/12、Schedule 19/19、Checkpoint 18/18、Readiness 16/16、Simulator 22/22 PASS；本报告未重跑。

## 数据与交接

测试数据只使用 Next Workspace 内最小 fixture；未复制完整 Site 或真实数据根。未访问旧 NetConsole 的 `D:\NetConsoleData` / `D:\NetConsoleData-dev`，未将配置原件、数据库或大型日志写入本报告。运行实例 PID、浏览器和实际 DB checkpoint 的最终验收尚未提供完成证据。

参考：[R7.0 配置状态机与受控设备证据](../r7-autonomous/R7_CONFIG_MANAGEMENT_REPORT.md)、[R7.2 恢复前检查](../r7-2-autonomous/R7_2_CONFIG_RESTORE_REPORT.md)、[R6.9 Writer 报告](../r6-autonomous/R6.9_CONFIG_RESTORE_WRITER_REPORT.md)。
