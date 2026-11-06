# Netmiko 连接盘点与后续工作

状态：`PARTIAL / REAL_DEVICE_PENDING`。本记录以当前源码和本批测试为准；历史 checkpoint 描述的是当时的状态。

## 原版参考

| 能力 | 主要位置 |
| --- | --- |
| SSH/Telnet 连接目标、协议尝试、超时和错误分类 | `src/netconsole/services/netmiko_connection.py`、`connection_manager.py` |
| H3C/ZTE SSH 与 Telnet 驱动 | `netmiko_connection.py` 的 `hp_comware`、`hp_comware_telnet`、`zte_zxros`、`zte_zxros_telnet` |
| 命令、分页和交互输出处理 | `netmiko_connection.py` 的 `safe_send_command` 和 `safe_send_command_with_paging` |
| SSH Host Key 和中转 | `host_key_trust_service.py`、`host_key_manager.py`、`ssh_tunnel.py` |
| 采集的命令选择和业务解析 | `device_operation_service.py`、`device_collection_support.py` 和各业务 parser |

原版实现有针对设备的编码与输出经验，不能仅凭驱动存在就宣称所有型号兼容。

## Next 入口与差距

| 入口 | 当前状态 |
| --- | --- |
| H3C 采集和设备连接测试 | 默认 Netmiko worker；密码按 H3C profile 读取，首次/变化的 Host Key 在认证后记录，包括旧 STRICT 档案 |
| SSH/Telnet Web Shell | 共用 worker 交互通道；显式允许的 AUTO 回退支持 SSH 协商失败和超时；SSH 支持远端 PTY 尺寸调整，Telnet 不支持远端调整 |
| ZTE 采集 | 走 Netmiko 的 `zte_zxros`，使用对应 Site 中转；独立 ZTE 档案保存读取密码并在认证后记录和轮换 Host Key；设备管理页已提供档案新增、凭据更新和采集入口，本机 API/组件回归通过 |
| Site Jump Host | Settings 提供完整配置、连接测试及指纹操作；已保存密码自动使用，成功后记录首次指纹及变化，防止配置变化期间写入旧结果；真实回环 direct-tcpip 转发、采集和 Web Shell 验收通过；Telnet 不套用 SSH 中转 |
| 驱动 | 由设备记录厂商和明确的 Cisco 平台选择；未知映射拒绝；映射和安装验证不是型号实机兼容证明 |
| 旧连接实现 | SSH.NET 的旧设备 transport 仍存在供显式测试和历史代码使用；当前 H3C、ZTE 默认采集及网络终端入口不使用它。中转转发和探测使用 Netmiko 依赖 Paramiko 的 SSH 通道 |
| 离线运行包 | Windows x64 CPython 3.13 隔离构建、搬迁校验及回环 Telnet 已验证；Service 候选组合构建通过，正式安装和其他机器验收待完成 |
| Linux 服务器与交互终端 | 独立 inventory 和 SSH.NET ShellStream；loopback Paramiko 模拟器 HTTP/WebSocket 及 Edge 浏览器 E2E 通过，含静默撤权后释放 SSH。Linux 真实主机仍待验收；服务仍限 loopback，跨机访问未实现 |

自动信任和轮换不提供独立的设备身份验证。Telnet 是明文传输；启用自动回退后，SSH 超时也会尝试配置的 Telnet 目标。

## 当前业务入口盘点（2026-11-06 源码核验）

以下行号对应本次源码核验快照，用于定位入口和边界，不作为设备型号兼容或最终测试通过证明。

### 统一生产调用链

- `src/NetConsole.Next.Service/Program.cs:51` 将 `IDeviceSessionFactory` 注册为 `H3cSshDeviceSessionFactory`；`:58` 的采集调度器配置并发 2、队列容量 128。
- `src/NetConsole.Next.Service/H3cSsh.cs:717` 的 session factory 默认使用 `NetmikoWorkerH3cSshTransportFactory`（`:720`）；普通采集由 session 的 `ConnectAsync` / `ExecuteAsync` 调用 transport。transport 在 `:240` 从设备记录解析厂商，在 `:360` / `:438` 定位 CPython 和 worker。
- `tools/netmiko_worker.py:47` 集中保存协议与厂商的驱动映射；`:67` 校验显式驱动已安装且协议匹配，未知映射拒绝；`:196` 的 `ConnectHandler` 是普通网络设备 CLI 的实际连接入口，`:223` 使用 Netmiko `send_command`。
- `src/NetConsole.Next.Service/BoundedCollectionScheduler.cs:328` 创建统一 session，`:331` 连接，`:334` 起依次执行计划命令，`:337` 调用统一 session，`:338` 在结果保存前处理配置脱敏。业务命令、解析、Raw 和 SQLite 仍在服务侧。

### 已有业务与明确缺口

| 业务 | 已核验入口与调用链 | 当前边界 |
| --- | --- | --- |
| 新增设备后自动测试、单台连接测试 | `web/src/views/DevicesView.vue:133` 保存连接档案，`:141` 自动调用测试；详情测试 `:186` / `:191`，行内快捷测试 `:212` / `:216`。HTTP 为 `Program.cs:1055`，`:1063` 调用 `H3cConnectionTestService.TestAsync`；该服务在 `H3cConnectionTestService.cs:41` / `:42` 创建和连接统一 session | SSH 登录后在 `H3cConnectionTestService.cs:52` 执行 H3C `display version` 并按 H3C 解析；信息识别失败保留连接成功结果。Telnet 在 `:46` 仅返回认证结果。其他厂商驱动存在不等于已有对应厂商的信息识别 |
| 批量连接测试 | `DevicesView.vue` 的“批量测试连接”只提交已选库存 UUID；`POST /api/system/devices/connection-tests` → `DeviceConnectionTestService.Start` → 现有 `H3cBatchTaskService` / 有界调度器 / Netmiko，固定执行只读 `display version`。沿用 task-plans 的读取、report 和 cancel | 仅 SUPERADMIN、1–64 个显式唯一 ID、已启用且精确关联有效档案的 H3C 独立设备；FIT-AP 保持 AC 管理。合法请求先完成整批可见性检查，不存在/隐藏目标优先 404；不支持或关联不一致整批 409，预检失败零任务。UI 展示逐目标/命令结果并处理 Site、模块、权限及页面生命周期的迟到响应。Vue 82 项、Chrome mock 4 组、后端 harness 8 组与真实 HTTP/Netmiko/合成 SSH 14 项通过；创建任务前再次核对冻结库存映射，关联重绑返回 409 零写入，设备兼容仍为 `REAL_DEVICE_PENDING`，见 [批量连接专项验收](DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md) |
| 多设备、多命令采集计划 | `Program.cs` 的 `/api/system/h3c/task-plans` → `H3cBatchTaskService.CreateAndSchedule`；允许 1–64 个显式档案目标，校验 H3C 命令，创建各目标定义并提交统一调度器。单定义接口仍经同一调度器 | 这是已有批量采集基础；专用批量连接入口复用该计划、调度、报告与取消能力。任务页当前仍只选择一个档案；不能据此扩大其他厂商的命令/解析支持范围 |
| 设备详情刷新、Facts 刷新 | `Program.cs:398` 的库存详情从 Catalog 读取；`:1160` 的 H3C Facts API 读取已持久化事实；`web/src/views/H3cView.vue:28` / `:34`、`:54` / `:63` 刷新这些 API | 当前刷新不会重新发起 CLI 采集；需要通过任务定义/计划执行采集后再读结果 |
| H3C 设备信息和接口 | `H3cComware.cs:9` / `:12` 定义版本和设备命令，`:10` / `:11` 定义接口摘要和详情；通过任务定义/计划 → 统一调度器 → Netmiko。解析分派在 `:376` / `:382` / `:388` / `:474` | 命令与解析均为 H3C 业务；不能因选择了其他厂商驱动就宣称其采集兼容 |
| ZTE 设备信息和接口 | `Program.cs:1424` / `:1427` → `ZteCollectionService.Start`；`ZteCollection.cs:240` / `:243` / `:248` 创建、连接、执行 session。`:151` 默认使用同一 Netmiko transport；`:94`–`:98` 显式选择 SSH `zte_zxros`。`:295` / `:296` 调用对应业务 parser | 独立 ZTE 采集流程当前固定 SSH；worker 的 ZTE Telnet 映射不代表此独立业务已提供 Telnet 入口 |
| VLAN 采集 | `H3cComware.cs:25`–`:30` 的全部允许命令没有 VLAN；`Phase9VendorParsers.cs:402`–`:408` 的 ZTE 命令也没有 VLAN；对服务、worker 和 Web 源码搜索未发现 `display vlan` / `show vlan` 采集命令 | **缺口**：当前没有已实现的网络设备 VLAN CLI 采集/对应解析链。网络域、VLAN 规划与手工维护数据不构成设备 VLAN 采集 |
| LLDP 采集 | H3C 命令在 `H3cComware.cs:20` / `:21`，parser 分派 `:440` / `:446`，通过统一调度器；ZTE 在 `ZteCollection.cs:132` / `:133` 执行两条 LLDP 命令，`:298` / `:299` 分派 parser | H3C 与 ZTE 的已实现采集都共用 Netmiko；读关系/诊断接口与主动采集需要区分 |
| 光模块采集 | H3C `H3cComware.cs:22` 的 `display transceiver diagnosis interface` → `:452` / `:454` → `Phase9VendorParsers.cs:367` 的 `ParseTransceiverDiagnosis`，沿统一任务链执行；ZTE `ZteCollection.cs:132` 的 `show opticalinfo brief` → `:297` → `Phase9VendorParsers.cs:453` 的 `ParseOpticalBrief` | **已有业务命令和 parser**，均共用 Netmiko。H3C 任务页 `TasksView.vue:12`–`:15` 当前只展示版本、接口摘要、设备、LLDP 和 FIT-AP 五类快捷命令，光模块可由允许的任务定义/计划 API 提交；此项不等于每个型号均已实机验证 |
| AC、FIT-AP | `H3cComware.cs:13`–`:19` 的 AC WLAN 命令沿统一任务链；AP 列表/地址/射频 parser 在 `:394` / `:400` / `:406`。`WirelessReadModel.cs:595` / `:604` / `:616` 读 AC 已采集事实，`:635` 构造 AP 观察 | FIT-AP 按 AC 关系读取，不单独建立 CLI session；`DevicesView.vue:200` 拒绝独立新建 FIT-AP，配置备份也拒绝该目标 |
| AP Group 视图相关采集 | `Program.cs:1668` / `:1674`、`:1683` / `:1689`、`:1727` 的组/成员入口读 `GetGroupSnapshot`。`WirelessReadModel.cs:636` 使用 AP 列表行中的 Group；`WirelessGroupProjection.cs:45`–`:51` 按同一 AC、来源修订和时间派生临时组关系 | 当前生产 Catalog 在 `WirelessReadModel.cs:641` 未提供独立 GroupEvidence。组名只形成 `CANDIDATE`（`WirelessGroupProjection.cs:56`–`:60`），不构成稳定厂商组键或确认的跨 AC 组。未发现独立 AP Group CLI 采集命令；Fixture 可提供显式组证据（`WirelessReadModel.cs:558`） |
| 配置采集与备份 | `Program.cs:769` → `ConfigBackupService.Start`；`ConfigBackupService.cs:278` / `:279` 创建 `display current-configuration` 任务，`:281` 交由同一调度器；后续读取完整任务 Raw 保存备份 | `ConfigBackupService.cs:120`–`:128` 明确只支持独立管理的 H3C；其他厂商与 FIT-AP 返回不支持。下载/历史/diff 读取持久化快照，没有另开 SSH |
| 设备诊断 | `Program.cs:739` → `NetworkDiagnostics.cs:198`；支持种类在 `:55`，ICMP/TCP/DNS 分别在 `:210` / `:225` / `:232`；LLDP 在 `:247` 只查询已采集 H3C Facts | 当前没有主动发 CLI 的诊断业务。TCP 端口探测的 `TcpClient`（`:227`）不是另一套设备 CLI 连接；不能把所有诊断都标为 Netmiko 采集 |
| 网络设备 Web Shell | `Program.cs:979` 创建会话 → `TerminalSessionManager.cs:378`；`:392` 对 SSH/Telnet 创建 `NetmikoTerminalTransport`，AUTO 回退 `:294` 仍创建它。`TerminalTransports.cs:204` 共用同一 worker 并开启 interactive；`:243` / `:257` 读写 Netmiko 通道；worker 实际读写在 `netmiko_worker.py:241` / `:264` | 共用设备凭据、厂商选择和 Site Jump 配置；不另建普通网络设备认证客户端。交互驱动支持不等于对应厂商的结构化业务采集已实现 |

### 驱动可选与业务支持的区别

`tools/netmiko_worker.py:47`–`:64` 映射 H3C、Huawei、明确区分平台的 Cisco IOS / IOS-XE / NX-OS、ZTE、Ruijie 和 Juniper 的 SSH/Telnet 驱动；`DeviceManagement.cs:66` 从关联库存选择厂商/平台，Cisco 未明确平台时不会静默选 IOS。映射只有在当前安装的 Netmiko `CLASS_MAPPER` 存在对应驱动时才接受（`netmiko_worker.py:74` / `:82`）。

当前结构化网络 CLI 业务主要是 H3C 命令计划/解析与 ZTE 五条命令采集。Huawei、Cisco、Ruijie、Juniper 的驱动选择与交互连接能力，不能扩写成这些厂商已有信息、接口、VLAN、LLDP、光模块、AC 或配置备份全功能兼容。未知型号及缺少证据的型号继续标记 `REAL_DEVICE_PENDING`。

### 绕过 Netmiko 检查

静态核验未发现当前默认生产网络设备 CLI 入口绕过 Netmiko；仍存在以下明确边界：

- 旧 SSH.NET 实现尚留在源码：`H3cSsh.cs:200` / `:203` 的旧工厂、`:450` / `:470` 的旧 transport，以及 `:603` 的旧回显/分页收集器；`TerminalTransports.cs:36` / `:171` 的旧网络终端 transport。当前生产 DI 和网络终端创建分支没有选择这些实现，不能将“默认入口已收口”写为“第二套旧实现已全部删除”。
- `netmiko_worker.py:138` 的 Paramiko 客户端只建立 Jump Host SSH 转发，`:160` 打开 `direct-tcpip`，目标设备仍由 `:196` 的 Netmiko 连接；`:301` / `:302` 是 Jump Host 连接探测，不是普通网络设备 CLI 客户端。
- `TerminalSessionManager.cs:381` 与 `LinuxSshTerminalTransport.cs:21` 的 SSH.NET 用于独立 Linux 服务器需求；串口和 `NetworkDiagnostics.cs:227` 的 TCP 探测也不属于网络设备 SSH/Telnet CLI 入口。

原始入口盘点仅进行了源码读取和入口搜索。批量连接入口在本批新增，已补充前端验证；其后端与设备验收状态以 [专项验收](DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md) 为准，不覆盖下列历史 checkpoint 的验证范围。

## 验证范围

完整 Service harness PASS；Vue 类型检查和 8 files / 23 tests PASS；`netmiko_worker_test.py` 20 项、离线打包单元测试 4 项、Service 候选组合测试 3 项 PASS。Windows 离线包隔离运行、搬迁清单和回环 Telnet 通过。网络终端浏览器测试使用 loopback 合成 SSH/Telnet 服务，覆盖登录、命令、中文、AUTO 回退、COM、分屏、resize、重连及录制回放。Linux 使用当前 Vue Webroot 和 loopback Paramiko 模拟器通过 HTTP/WebSocket 与 Edge 浏览器门禁；Jump 使用真实回环 SSH 转发通过 HTTP/Netmiko/WebSocket/重启门禁，Settings Chrome 浏览器另使用 mock API 验证界面。以上模拟环境证据不等同真实设备、真实 Linux 主机、现场网络或其他 Windows 环境验收。

2026-11-06 当前 Netmiko `hp_comware` 已对 HCL AC1/AC2 执行版本及 AC AP 查询，对 SW 执行版本、接口摘要和 LLDP 查询，均返回有效输出。证据位于 Workspace `reports/netmiko-simulator-20261106.json`。FIT-AP 保持 AC 管理，不要求独立登录；历史 SW Exec 与独立 FIT-AP Telnet 失败不再是当前路径阻断。双 AC HA、切换、恢复及 AP 实时归属仍待验证，远端配置写入为 0。最新批次验收与限制见 [Web/Linux/Jump 验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)。
