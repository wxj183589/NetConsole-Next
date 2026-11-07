# 原版 NetConsole → NetConsole-Next 功能迁移矩阵（R7.1）

更新时间：2026-11-08；审计范围为 R7.1 集成工作树，基线 `bdcd4f9`，分支 `codex-A/netconsole-next-r3-dual-ac`。整体 **PARTIAL**。本表替代旧 R6.1 汇总；各项均按源码、API、UI、测试和可操作范围分别判断，不把页面存在或测试存在当作全功能迁移完成。

原版源码根：`D:\study\NetConsole-Workspace\NetConsole`；Next 源码根：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`。本轮只读原版 `src` / `apps`，未启动原版，未访问旧真实数据根。下表原版路径相对原版根；Next API 省略共同前缀 `/api/system`。测试列指明确的覆盖入口，最终执行结果以 [R7.1 最终验收](../r7-1-autonomous/R7_1_FINAL_ACCEPTANCE.md) 为准；未在本项重新执行的旧测试不冒充本轮实测。

状态：**PASS** 仅用于具名验证结果；**PARTIAL** 表示有可用子链但未达到原版完整语义；**BLOCKED** 表示外部条件或真实能力阻断；**NOT_TESTED** 表示本轮未执行对应实机/安装验收。

| # / 功能 | 原版是否存在及源码证据 | Next 实现 / API | UI 入口 | 测试 / 实际操作证据 | 结论、边界和下一步 |
| --- | --- | --- | --- | --- | --- |
| 1 设备管理 | 有；`src/netconsole/backend/api/device_management_router.py`、`services/device_management_web_service.py` | `DeviceManagement.cs`；`GET/POST /devices`、`PUT /devices/{id}`、分组、导入、批量预览确认 | `/devices` 的 Site、分组、筛选、编辑、详情 | `DevicesView.test.ts`、`DevicesConnectionTests.test.ts`；真实浏览器加载授权库存 | PARTIAL：资产 CRUD 可操作；本轮详情接入真实能力证据，现场型号/资产全量对齐未验 |
| 2 设备连接 | 有；`services/connection_manager.py`、`services/netmiko_connection.py` | `H3cConnectionTestService.cs`、Netmiko worker；`/h3c/profiles/{id}/test-connection`、`/devices/connection-tests` | `/devices` 添加并测试、快速连接、批量连接 | `DeviceConnectionTests.cs`、Worker 测试；AC1/AC2/SW 真实 SSH 认证通过 | PARTIAL：复用 Netmiko；FIT 已有凭据认证失败，不猜测或重复撞密 |
| 3 SSH/Telnet 控制台 | 有；设备连接服务和 external-terminal 入口 | `TerminalSessionManager.cs`、`LinuxSshTerminalTransport.cs`；`/terminal/sessions`、WebSocket；Linux 独立端点 | `/terminal`、`/linux`，网络设备与服务器分开 | `TerminalCoreTests.cs`、`LinuxServerTests.cs`、`TerminalView.test.ts` | PARTIAL：已有控制台/录制回放；Linux 与所有现场 Telnet 目标互操作 NOT_TESTED，不能以 SSH 通过代替 |
| 4 SSH 中转 | 有；`services/site_ssh_relay.py`，Site scoped direct-tcpip | `NetmikoJumpHostProbe.cs` / worker；`/sites/{siteId}/ssh-jump-host` 及 test/fingerprint | 现有连接/局点配置入口，终端复用 | 既有 Jump/连接策略测试、Worker 测试 | PARTIAL：配置、指纹和共享入口存在；本轮无真实跳板拓扑，不代理 ICMP/UDP/Syslog |
| 5 设备信息采集 | 有；H3C command profile、`services/ac/` | `H3cComware.cs`、Raw→Facts→Current；`/h3c/task-plans`、`/h3c/profiles/{id}/facts`、`operational-capabilities` | `/h3c`、任务中心、设备详情型号/版本/能力 | `H3cTaskFactPublicationTests.cs`、`H3cOperationalCapabilityTests.cs`；AC WX5540H-HCL/Comware 7.1.064 实读 | PARTIAL：已测设备真实采集；无证据 UNKNOWN，过期 STALE，不能把 fixture 当在线 |
| 6 接口管理 | 有；`parsers/h3c/interface_parser.py`、`services/interface_discovery_routing.py` | `H3cFacts.cs` 接口事实、能力投影；facts/task-plans | `/h3c`、设备详情接口读取、健康工作台 | 解析/发布测试、`OperationalHealthTests.cs`；SW 实读 brief，AC只读补采通过 | PARTIAL：接口查询和健康观测可操作；未迁移完整接口编辑/性能/错误趋势工作台 |
| 7 VLAN | 有；`core/sources/vlan.py`、厂商解析器、AP management VLAN planning | `H3cVlans.cs`、`/vlan-plans`、网络规划 CRUD；H3C VLAN facts | `/network-planning`、`/h3c`；智能配置 Access VLAN 仅预览 | `H3cVlanTests.cs`、P2B4/P2B5 测试 | PARTIAL：规划数据与设备运行 VLAN 是不同对象；无通用真实 VLAN Writer |
| 8 LLDP | 有；`parsers/h3c/lldp_parser.py`、`services/ap_topology/` | `H3cLldpTopology.cs`；`/h3c/profiles/{id}/lldp-relations`、`lldp-claims` | `/topology`、设备详情能力证据、工具集 LLDP | `H3cLldpTopologyTests.cs`5/5、publication17/17、`TopologyView.test.ts`；SW真实3邻居，其中MAC名称邻居不推断确定AP身份 | PARTIAL：来源/捕获时间/claims 可查，空态不伪造邻居；全现场消歧未验 |
| 9 网络拓扑 | 有；`services/ap_topology/`、Mesh catalog | 库存 + LLDP claims + 无线 Domain topology API | `/topology`、无线 Domain | `TopologyView.test.ts`、无线测试；浏览器库存/空LLDP状态 | PARTIAL：只读拓扑资产工作台；已增加精确profile/本地接口/Site筛选与当前LLDP CSV（含来源/时间/revision/partial）；真实SW重采3正确Current，浏览器精确profile/接口/Site筛选1行及820bytes CSV PASS；同identity跨来源/scope Current继承限制仍在；完整图布局、跨站拓扑和长期变化分析未迁移 |
| 10 无线 AC 管理 | 有；`backend/api/ac_management_router.py`、`services/ac/` | `WirelessReadModel.cs`、Domain/AP Observation、`/wireless/ac-domains`、`/wireless/operational-capabilities` | `/wireless`，AC导航、Domain、观测、任务历史 | Wireless/Runtime/Task测试；两AC真实 Common，同AP独立 R/M、R/B | PARTIAL：USER_CONFIRMED_TOPOLOGY、DEVICE_REPORTED_ROLE分离；DEVICE_VERIFIED_HA未取得，切换 NOT_TESTED |
| 11 FIT-AP 管理 | 有；AC 路由、`services/ap_identity/`、FIT AP parsers | AP Resource/Observation/identity/history API | `/wireless` AP详情；设备页禁止独立创建FIT | Wireless Identity/Runtime测试；真实 WA6320-HCL MAC/序列号/双AC观测 | PARTIAL；AC托管可用，直接Telnet BLOCKED/AUTH_CREDENTIAL_UNAVAILABLE，不把AC观察当FIT原生登录 |
| 12 AP Group | 有；AC资源命令和组成员查询 | `WirelessReadModel.cs` / source键；`/wireless/ac-domains/{id}/ap-groups`、`/wireless/ap-groups/{id}/members` | `/wireless` 的组与成员 | `WirelessGroupTests.cs`、`WirelessView.test.ts` | PARTIAL：保留来源AC与revision；组名相同不自动共享，完整权威生命周期待补 |
| 13 Mesh | 有；`backend/api/ac_mesh_link_router.py`、`mesh_analysis_router.py`、Mesh分析服务 | 现有命令/raw/通用事实和有限无线投影；无对等 Mesh 专用分析 API | 可查原始任务/事实；无原版 Mesh 专项分析页面 | 原有解析/回放可覆盖部分命令；本轮无端到端 Mesh 迁移验收 | PARTIAL：链路时序、质量、掉线分析及Excel专项报表未完整迁移；不能以LLDP拓扑替代 |
| 14 Ping | 有；`services/network_tools/`、`ground_unattended/fleet_ping.py` | `NetworkDiagnostics.cs`、`/diagnostics/runs`，PING/TCP/DNS/TRACEROUTE | `/tools` 选明确设备执行、取消、历史 | `NetworkDiagnosticsTests.cs`、`NetworkTracerouteTests.cs`、ToolsView测试 | PARTIAL：有界主机探测可操作；不等于原版车队长期监测；Traceroute已有界限/部分路径 |
| 15 Quick Ping | 有；`ground_unattended/quick_ping.py`、`QuickPingPage.vue`，fping v5；不限时或最多7天 | 本轮 `NetworkQuickPing.cs`；`/diagnostics/runs` 的 QUICK_PING；.NET ICMP/TCP | `/tools` 采样参数、进度、逐样本、丢失率/min/max/avg | `NetworkQuickPingTests.cs`、ToolsView测试；真实浏览器 AC1 TCP三样本成功 | PARTIAL：2–60样本、最多8目标、每目标120s预算、100–5000ms间隔/超时；TCP指标标明TCP；长期fping/保留压缩/IP占用语义未迁移 |
| 16 Syslog | 有；`src/netconsole/services/ground_unattended/syslog_runtime.py`、`SyslogPage.vue` | `UdpSyslogWorker.cs`、`SyslogQuery.cs`、`H3cSyslogEventClassifier.cs`；`POST /syslog/query`，封存Raw长度/hash/路径验证；8段/100000记录/500条有界查询 | `/tools` 接收诊断、时间/IP/severity/文本/五类确切H3C事件过滤、原文/Raw、CSV | 查询8/8+事件6/6、Vue6/6；本机合成UDP→封存Raw→真实静态UI查询/CSV，冲突UNKNOWN，双分辨率0错误 | 已交付业务链PASS；整体PARTIAL，设备发日志、全部payload/身份关联、长期保留和负载尚未验 |
| 17 配置备份 | 有；`config_collection_router.py`、`config_lifecycle_service.py`，running/saved/compare/export | `ConfigBackupService.cs`、`SimulatorConfigurationService.cs`；`COMPLETE_RUNNING`批量/计划、立即执行，严格profile/Site/Domain冻结；原件与脱敏分离、版本/容量准入 | `/config-backups` 完整批量、计划/最近批次、历史、hash/比较/脱敏下载 | 完整批量11/11、计划19/19、完整配置19/19；AC1/AC2/SW真实hash；SW浏览器立即执行/暂停PASS | PARTIAL：完整批量/持久计划/受控重启已验；自然到期最终结果见最终报告，整机恢复仍BLOCKED；不自动删除历史 |
| 18 配置恢复 | 原版有配置生命周期与save force确认；本轮未从该源码证实任意历史整机恢复入口 | 旧 `/config-backups/restore/*` 有受限流程；完整私有 `restore-preview` 永远writeAllowed=false；checkpoint verify失败封闭 | `/config-backups` 预览、状态/阻断；原生检查点独立确认 | `ConfigRestoreTests.cs`、`H3cCheckpointRestoreTests.cs`；真实SW commit后无新增checkpoint | BLOCKED：NATIVE_CHECKPOINT_NOT_CREATED；native replace交互未验证，transport禁止；描述恢复不等于整机恢复，未宣称SERVICE/ROLLBACK全链通过 |
| 19 智能配置 | 未定位原版独立同名稳定 API/模板执行闭环；配置生命周期能力不能等同模板批量写入 | `SmartConfigurationService.cs`、`/smart-config/templates`、`/smart-config/preview` | `/smart-config` 两个H3C模板 | `SmartConfigurationTests.cs`、`SmartConfigView.test.ts`，变量/接口/VLAN/注入校验 | PARTIAL：已实现 DRY_RUN_ONLY；不连接、不写设备，无批量执行/回滚 |
| 20 批量任务 | 有；`job_center_router.py`、`task_router.py`、`services/job_center/` | `H3cBatchTaskService.cs`、TaskPlan API、批量连接、脱敏备份批次 | `/tasks`、设备页、配置页 | `TaskPlanTests.cs`、DevicesConnectionTests、TaskSiteScopeTests | PARTIAL：已有有界计划/并发/取消；未把完整私有备份、智能配置、任意恢复统一成已完成任务类型 |
| 21 任务历史 | 有；job center query/events/artifact | `/tasks`、commands/diagnostics/download、`/wireless/tasks`/retry/download | `/tasks`、`/wireless` 历史筛选与重试 | TaskExport/TaskDiagnosticEvidence/WirelessTask测试；真实无线任务ZIP/历史 | PARTIAL：可查任务/失败/Raw/Parse；全原版任务种类与长期历史策略未完全对齐 |
| 22 数据导入导出 | 有；设备导入、file_management、export_worker | `/devices/import/preview|commit`、`/import-runs`、`/network-planning/exports`、任务/无线ZIP | `/devices`、`/network-planning`、`/files`、任务下载 | P2B4/P2B5/TaskExport/WirelessTask测试 | PARTIAL：明确格式可操作；不自动读取旧目录，不迁移全站真实数据，原版全部Excel/业务格式未覆盖 |
| 23 故障分析 | 有；Mesh、MR、ground Syslog/network tools分析服务 | `FaultCenterService/Routes.cs`、既有health/recover；有界持久事件、指纹聚合、OPEN/ACKNOWLEDGED/RESOLVED历史、显式后台补采策略、最多3次、>=5min | 总览故障中心，设备精确筛选、配置历史/只读来源，策略/确认/新证据恢复 | FaultCenter14/14、UI13/13；真实浏览器精确筛选/任务证据/配置导航，0错误 | PARTIAL：正常连接/入队不当恢复，同资源新证据才可确认；未制造模拟器业务故障，真实恢复与通知覆盖待验 |
| 24 报表 | 有；`mesh_analysis_excel_report.py`、`vehicle_mr_offline_excel_report.py`、`online_mr_analysis_report_exporter.py` | 规划/任务/诊断/无线导出、完整配置比较脱敏JSON | 文件/任务/规划下载、配置差异导出 | P2B5Export、TaskExport、诊断ZIP完整性测试 | PARTIAL：具体导出可操作；无原版Mesh/MR等全部专业报表和统一报表设计器 |
| 25 设备状态监控 | 有；AC动态采集、fleet ping、ground syslog运行态 | `/operations/health`，Current/Raw/diagnostic投影、300000ms新鲜度、设备能力API | 总览健康分页、无线运行态、设备详情 | OperationalHealth/Capability/DevicesView测试；真实6台授权库存，缺失CPU/内存为UNKNOWN | PARTIAL：有证据状态和即时读取；故障ACK/聚合/有界策略与历史已补；完整趋势、接口错误、通知及长期自治闭环未全迁移 |

## R7 实际增量与验收边界

本轮实现了 Quick Ping 有界连续采样、健康/故障证据与受控只读补采、设备详情真实能力 API、无线角色/能力投影、完整配置上下文比较/恢复预览、原生检查点失败封闭、LocalManager doctor。设备详情定向13/13与typecheck通过；其余测试及最终浏览器复验按专项报告和最终Gate记录，不复用R6.9的234/234等数字表示R7。

真实设备来源：`reports/r70-device-20261107-233336`（两AC身份/角色）；`reports/r70-sw-checkpoint-20261107-234349`（commit无效果、hash未变）；`reports/r70-sw-checkpoint-20261107-234525`（接口/LLDP）。报告路径相对 Next Workspace。原生整机恢复、双AC切换、FIT直接登录均未取得成功证据。

优先级：先解决有真实设备支持的安全恢复与最终部署验收；再补完整私有备份批量/定时、Syslog viewer和长期Ping迁移合同；继续Mesh/专业报表、完整事件通知及资产/拓扑增强。详情见 [R7迁移报告](../r7-autonomous/R7_ORIGINAL_FEATURE_MIGRATION_REPORT.md) 和 [当前任务池](../full-day-autonomous/NEXT_TASK_BACKLOG.md)。

## R7.1 增量与证据

R7.1已实际交付完整私有配置批量/计划、Syslog查询/接收诊断/CSV、持久故障中心与可选补采、设备型号/版本/角色事实、拓扑精确筛选导出、运维关联入口和部署/Worker生命周期修复。配置计划立即执行与自然到期验收以最终报告为准。专项范围、实际与合成证据分开记录，未因此将25项全部标为完成。

上一节R7.0数字保留历史语义；R7.1结果以 `docs/r7-1-autonomous/` 中最终验收、测试证据和各专项为准。整机恢复、受控HA、FIT直接认证、Windows SCM/跨机器及物理空口业务仍不升级为PASS。未访问旧真实数据根，未Push/Merge/Release。
