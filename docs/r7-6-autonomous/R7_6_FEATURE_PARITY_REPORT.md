# R7.6 原版功能对等事实报告

日期：2026-11-08。Agent：codex-A。源码基线：`50db848`。总体结论：**PARTIAL**。

权威逐项比较仍为 [原版到 Next 的 25 项功能矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md)。该矩阵的审计基线为 R7.1 `bdcd4f9`，本报告记录当前主线增量及剩余条件，不把矩阵中的 PARTIAL 项、受阻恢复或未测现场能力批量升级为 PASS。

本报告只读复核 Next 主线源码、提交、矩阵和已有专项报告，没有启动原版、Service 或设备会话，也未读取旧 NetConsole 业务数据。测试文件和页面存在只说明实现与覆盖入口；实际执行结果和最终候选验收分别记录。

## 高价值已实现业务链与本轮增量

| 能力范围 | 当前已实现或本轮补齐 | 关键证据与边界 |
| --- | --- | --- |
| 资产、连接与任务 | 设备/Site/分组、导入预览确认、连接测试、受限 SSH/Telnet 控制台、任务计划/取消/历史/Raw 下载 | 复用既有 Device、Terminal、Netmiko、TaskPlan；设备型号和现场全量互操作仍按来源逐台验证 |
| 采集与状态证据 | Raw→Facts→Current、版本/接口/LLDP、能力与新鲜度投影；缺失 UNKNOWN、过期 STALE | `H3cFactPublication.cs`、`OperationalHealthService.cs`；连接成功或采集失败不能直接推断业务在线/离线 |
| LLDP 与拓扑 | 来源和 captured scope、精确 profile/接口/Site 筛选及有界 CSV | 不能以资产/LLDP 工作台替代完整 Mesh、跨站拓扑和长期图分析 |
| 双 AC 与 FIT-AP | AC Domain、精确 AP Resource/Observation、独立来源、Current/PreviousEffective/Recent10；新增当前 AC 三命令快速采集和 scoped AP 历史 CSV | `afcd4f0`、`09fe24f`；双 AC 六命令产品 Raw 实采另见设备报告；FIT 原生认证仍阻断，未取得 HA 切换证据 |
| Quick / Long Ping | 有界 ICMP/TCP 采样、历史趋势、已回复延迟 nearest-rank P95、CSV；Long Ping 新增暂停/继续并复核权限 | `efdfa8b`、`d9041b3`、`cdaac2b`；Long Ping 10–3600 秒、最多 8 目标；暂停保留原墙钟截止，未执行样本不算丢包；TCP 结果保留 TCP 语义 |
| Windows Traceroute | 普通令牌下使用系统 ICMP tracert，显示实际 `WINDOWS_SYSTEM_ICMP_TRACERT` 来源；有界输出、取消/撤权回收子进程 | `50a0b0b`、`af1b63b`、`ce7032d`、`50db848`；修复终点跳可能吞掉已产生 stderr 超限错误；主机原生拒绝仍显式失败 |
| Syslog 与故障中心 | 有界 Raw 查询/事件/CSV；持久资源故障、筛选/确认/处理中/核验恢复/复发、默认关闭的只读补采 | 本轮 `bcdeb35` 防止取消任务制造失败告警；现有故障恢复必须有同资源更新证据，详见故障中心报告 |
| 配置管理 | 原件/脱敏分离、完整备份、批量/持久计划、比较/预览；补读写路径祖先 ReparsePoint 保护与严格 LLDP 恢复证据 | `3f65378`、`a36f23a`、`bf245ad`；历史模拟器描述 Writer 不等于整机历史恢复或故障回滚 |
| 规划、导出与智能配置 | VLAN/网络规划 CRUD、格式限定导入导出、任务诊断 ZIP、配置差异；模板变量与注入检查 | 智能配置仍 `DRY_RUN_ONLY`；不是任意设备批量 Writer，也未覆盖原版全部 Excel/专业报表 |
| 本机运维 | LocalManager doctor/readiness、服务安装计划、候选离线门禁；补无效 PID 和 Windows PowerShell 5.1 路径兼容 | `89682f8`、`39c2827`；安装计划与进程候选不能代替真实 elevated SCM 生命周期或跨机器安装 |

Long Ping 暂停请求先进入 PAUSING，当前 probe 排空后才为 PAUSED；暂停中仍复核当前授权和目标，不通过暂停延长持续窗口。历史 Ping 趋势只组合相同 actor/Site/device/protocol/endpoint/port 的可比样本；校验采样序号、计数和开始/结束时间，P95 来自实际回复延迟而非各任务 P95 的平均值。当前 trend CSV 范围为 `LOADED_SCOPED_HISTORY_ONLY_MAX_20_RUNS`，不能宣称完整长期历史。

## 仍影响完整对等的缺口

| 剩余能力 | 当前结论与下一步证据 |
| --- | --- |
| Mesh 专项分析 | PARTIAL；链路时序、质量、掉线分析、专项可视化及 Excel 报表未完整迁移；LLDP 不能替代 Mesh |
| 轨交 / MR 专业报表 | PARTIAL；车载 MR 离线/在线、列车地面业务及全部专业 Excel 流程未对齐；通用 CSV/ZIP 不等于专项报告 |
| 移动车辆与 AP 时间关联 | PARTIAL / NOT_TESTED；仍需同时间轴上的实际丢包、位置/站点、AP身份与角色变化证据；不得以管理 IP、同名 AP 或邻近时间猜测关联 |
| 长期监测 | PARTIAL；现有有界 Long Ping 与持久诊断不能代替原版不限时/多日车队 fping、完整保留压缩策略和长期负载稳定性 |
| 现场无线与设备写入 | BLOCKED / NOT_TESTED；有效 FIT 凭据、SW 认证、双 AC HA 切换/回切、客户端连续性、物理硬件 Writer 仍需独立证据 |
| 整机配置恢复 | BLOCKED；设备上传/哈希、native replace 交互、恢复后独立采集与故障自动 rollback 未取得完整证据 |
| 资产/接口/拓扑与通知 | PARTIAL；完整接口编辑/错误趋势、跨站拓扑、全资产生命周期和通知交付仍未完全迁移 |
| Windows 部署与跨机 | NOT_TESTED；elevated SCM 安装、停止/重启/恢复、独立 Windows 主机及长期服务运行仍待实际验收 |

## 当前验证与最终候选边界

本报告编写阶段未重新运行测试。源码存在 LongPingControl、QuickPing、WindowsSystemTraceroute、OperationalHealth、FaultCenter 与 Web 对应回归；它们的源码计数或输出常量不能作为本轮已通过结果。R7.5 的 353 项 Web 和历史 Service 结果保留其历史范围，不冒充当前 `50db848` 全量门禁。

| 最终门禁 | 状态 |
| --- | --- |
| 当前主线 Service / Web / typecheck / build | PENDING_ROOT_FINAL_ACCEPTANCE：主集成补录实际 commit、命令、计数和退出码 |
| 最终候选离线与 Long Ping HTTP 验收 | PENDING_ROOT_FINAL_ACCEPTANCE：主集成补录制品路径、启动方式、ICMP/TCP 来源与实际结果；fixture 或 TCP 不升级成实机 ICMP |
| 最终候选双分辨率浏览器 | PENDING_ROOT_FINAL_ACCEPTANCE：主集成补录候选运行实例、截图和下载/操作证据 |
| 完整 25 项原版语义与独立实机验收 | PARTIAL；本报告不宣称全部完成 |

仅新增事实文档，没有修改共享源码、启动服务、设备写入或复制 Site/数据库。相关：[设备互操作](R7_6_DEVICE_INTEROP_REPORT.md)、[无线](R7_6_WIRELESS_REPORT.md)、[FIT-AP](R7_6_FIT_AP_REPORT.md)、[配置恢复](R7_6_CONFIG_RESTORE_REPORT.md)、[Windows 部署](R7_6_WINDOWS_DEPLOYMENT_REPORT.md)、[故障中心](R7_6_FAULT_CENTER_REPORT.md)。
