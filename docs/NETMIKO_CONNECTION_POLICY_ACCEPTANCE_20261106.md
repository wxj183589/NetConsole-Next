# Netmiko 连接策略与原版能力对照验收

日期：2026-11-06。分支：`codex-A/netconsole-next-r3-dual-ac`。本批接续 `e12d06203cb5ad4a2d903049338ecb4df2a935d1`；功能基线以本文所属的连接与批量测试功能提交为准，最终候选另记于离线部署文档。

状态：**连接策略及共享 worker 预算的定向回归通过；完整业务继承仍为 PARTIAL / REAL_DEVICE_PENDING。** 本文记录连接策略修复及其验收；完整生产入口盘点见 [连接审计](NETMIKO_CONNECTION_AUDIT_20261106.md)。

## 本批问题与最终行为

本批核验发现，连接测试和 Web Shell 已有 AUTO 协议切换逻辑，但采集 session 没有相同的 SSH→Telnet 尝试。连接测试还保留单独的 Telnet 登录路径，使测试与实际采集的连接策略不一致。另一个缺口是，设备档案的 `AllowLegacyAlgorithms` 没有传给 Netmiko worker，原版已验证的 H3C 旧 Host Key 协商兼容参数没有生效。

进一步复核发现，H3C 调度、ZTE 调度和终端分别有并发限制，但 HTTP 单台连接测试仍可无限创建 worker。本批在实际进程启动位置增加了每个 Service 实例共享的 **16 个 CPython worker 名额**，覆盖采集、连接测试、网络 Web Shell 和 Jump Host 探测。

修复后的生产行为如下：

- 采集和连接测试共用 `H3cSshDeviceSession`；Web Shell 使用同一轻量 `H3cConnectionPolicy` 判定是否切换协议。
- `AUTO` 且显式允许 Telnet 回退时，SSH 协商失败或连接超时最多触发一次 Telnet 尝试，发生在业务命令执行前。显式 SSH、关闭回退和显式 Telnet 均遵循档案配置。
- 认证失败、Host Key 错误、Jump Host 错误、未知驱动、普通 IO 错误和取消均不触发此协议切换。调用者取消后不再开始新尝试。
- 每次失败先释放 transport 并终止待确认的 Host Key 状态。密码按设备档案读取一次，连接测试返回最终实际协议；Telnet 认证测试不额外执行设备信息命令，也不重复登录。
- `AllowLegacyAlgorithms` 为真正的布尔 `true`、驱动为 SSH `hp_comware` 且错误属于已识别的 Host Key 协商失败时，worker 先关闭失败连接，再用原版参数 `disabled_algorithms={"keys":["rsa-sha2-512","rsa-sha2-256"]}` 尝试一次。第二次失败即结束。
- H3C 兼容尝试若经过 Jump Host，会重建 SSH 转发和 `direct-tcpip` 通道；已失败的通道不会复用。该路径仍由 Netmiko 登录目标设备。
- worker 名额在启动前等待，支持调用者取消；连接的 45 秒期限及 Jump probe 的 30 秒期限均包含预算等待。启动失败归还名额；成功启动后由异步退出观察器在进程实际退出时归还，释放幂等。`Abort`、`Dispose` 和 probe 结束负责终止进程；终止请求失败且进程仍活着时继续占名额。
- transport 使用独立生命周期锁保护启动与终止，取得名额后复核取消和释放状态，同一实例只启动一次。预算没有复用 SSH 连接，也没有增加连接池或业务调度框架。

上述次数分别约束协议切换和 H3C Host Key 兼容尝试。它们没有实现断线后的任意命令重放、通用重连循环或自建连接池。

## 原版研究与复用范围

原版仓库 `D:\study\NetConsole-Workspace\NetConsole` 仅作源码只读参考；本批未读取或修改其真实业务数据根。

| 原版能力 | 已研究入口 | Next 的继承方式与边界 |
| --- | --- | --- |
| Netmiko 连接、协议目标与错误处理 | `src/netconsole/services/netmiko_connection.py` 的 `connection_targets`、`run_netmiko_with_retry`、`check_device_login_with_netmiko`；`connection_manager.py` 的 `_device_protocol_attempts` | 保留 Next 的档案、任务及异常模型，用轻量 session 调用同一个 CPython Netmiko worker；新增有界 AUTO 协议尝试 |
| H3C 旧 Host Key 协商兼容 | `netmiko_connection.py` 的 `_connect_with_compatibility`、`_legacy_ssh_rsa_allowed`、`_legacy_ssh_rsa_params`、`_is_host_key_negotiation_error` | 复用原版作用范围与参数；补充 Netmiko 包装的 Paramiko 异常识别，不因普通超时、认证、kex 或 cipher 错误触发旧 Host Key 参数 |
| H3C、ZTE 驱动与业务能力约束 | `netmiko_connection.py` 的驱动常量；`device_collection_support.py`；`h3c_collect_service.py` | 使用明确的 Netmiko SSH/Telnet 驱动，不把任意设备静默归到 H3C 或 ZTE；命令与解析仍由 Next 服务管理 |
| 回显、分页与命令执行经验 | `netmiko_connection.py` 的 `safe_send_command`、`safe_send_command_with_paging`；`h3c_collect_service.py` 的命令与 fallback 选择 | Next worker 使用 Netmiko `send_command` 和驱动准备；没有移植原版手工分页循环，避免再次实现终端分页机制 |
| AC、FIT-AP 和 AP Group | `h3c_ac_collect_service.py` 的 AC 命令集、Netmiko 建连及组证据处理；`services/ac/ac_command_service.py`、`ac_service.py`、`ac_optical_service.py` | Next 保留 AC 采集与事实投影；FIT-AP 通过 AC 管理。原版完整命令集和稳定组证据尚未全部迁移 |
| 配置生命周期 | `config_lifecycle_service.py` 的 `display current-configuration`、`display saved-configuration` 和统一重试入口 | Next 当前实现独立 H3C 的运行配置备份；保存配置、启动配置和完整生命周期尚未继承 |
| CLI 诊断文件下载 | `diagnostic_download_service.py` 的 `display diagnostic-information` 和统一重试入口 | Next 现有诊断不包含该 CLI 导出，明确保留为业务缺口 |

原版允许 SSH 与 Telnet 各自保存端口、用户名、密码。Next 当前档案只有一组主机、端口、用户名、密码及协议；AUTO 的 Telnet 尝试使用同一档案端点。本批没有扩展为两套协议端点或两套凭据，不能宣称这项参数模型已与原版等同。

## 当前业务入口对照

以下是生产入口覆盖，不代表所有命令、型号或现场场景均已完成验收。

| 用户要求的入口 | Next 当前调用与状态 | 未完成或需区分的部分 |
| --- | --- | --- |
| 新建设备连接测试 | 设备页保存档案后调用 `H3cConnectionTestService`，统一经 `IDeviceSessionFactory` → Netmiko；密码自动读取 | SSH 的设备信息识别仍使用 H3C `display version` 及解析；驱动存在不代表具备对应厂商的信息解析 |
| 设备详情刷新 | 详情和 Facts API 读取 Catalog 已存事实 | 点击刷新本身不会触发新的 CLI 采集；需先执行采集任务 |
| 批量连接测试 | 设备页明确选择库存 ID → 专用 `devices/connection-tests` API → 现有任务计划与统一 Netmiko 调度；固定只读 `display version` | 仅 SUPERADMIN、已启用且精确关联档案的独立 H3C 设备；整批可见性预检优先，拒绝 FIT-AP 和不支持的目标。证据见 [专项验收](DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md) |
| 设备信息、接口 | H3C 命令计划 → `BoundedCollectionScheduler` → `H3cSshDeviceSession`；ZTE → `ZteCollectionService` → 相同 Netmiko transport | H3C 与 ZTE 有各自业务命令及 parser；其他厂商的完整结构化采集仍待实现和验证 |
| VLAN | 现有 VLAN 规划、导入、接口 VLAN 字段 | **缺口：无生产 `display vlan` / `show vlan` 采集及完整解析链** |
| LLDP | H3C 任务命令及 ZTE LLDP 命令共用 Netmiko | LLDP 诊断读取已存 Facts，与主动采集不同 |
| 光模块 | H3C `display transceiver diagnosis interface` 与 ZTE `show opticalinfo brief` 已有命令与 parser，共用 Netmiko | H3C 可从允许的任务定义/计划 API 提交；任务页快捷命令不覆盖全部命令；型号实机待验证 |
| AC 与 FIT-AP | H3C AP 列表、地址、射频及相关 WLAN 命令走统一任务链；`WirelessReadModel` 从 AC Facts 构造 AP 观察 | FIT-AP 不独立登录。完整原版 AC 命令集、双 AC HA、切换恢复及实时归属仍待验收 |
| AP Group 视图 | 从同一 AC 的 AP 观察、组名及证据派生关系 | 当前生产投影缺少独立稳定 GroupEvidence；组名候选不等于已确认组身份；未发现独立 AP Group CLI 采集命令 |
| 配置采集与备份 | `ConfigBackupService` 创建 H3C `display current-configuration` 任务，交给统一调度器；历史、下载与 diff 读取快照 | 当前仅支持独立管理的 H3C，FIT-AP 和其他厂商拒绝；未实现原版启动配置与配置保存流程 |
| 设备诊断 | `NetworkDiagnostics` 提供 PING、TCP、DNS 和已存 LLDP Facts | TCP 探测不是 CLI 客户端；未实现 `display diagnostic-information` 下载 |
| 网络设备 Web Shell | `TerminalSessionManager` 对 SSH/Telnet 创建 `NetmikoTerminalTransport`，复用 worker 交互通道、设备凭据和 Site Jump | 交互连接可用不代表对应厂商结构化采集均已实现 |
| Linux 与 Jump Host | Linux 独立 SSH.NET shell；Jump Host 的 Paramiko SSH 只建立转发，目标设备仍由 Netmiko 登录 | 它们属于独立 Linux/中转需求；真实 Linux 主机和真实中转环境仍待验证 |

生产 DI 在 `Program.cs` 注册 `H3cSshDeviceSessionFactory`，默认 transport 为 `NetmikoWorkerH3cSshTransportFactory`；采集调度器并发上限为 2，队列容量 128。ZTE 默认同样使用 worker，并显式选择 `zte_zxros`。未发现当前默认生产网络 CLI 入口绕过 Netmiko。

这些业务限制保留，同时由 `NetmikoWorkerBudget` 约束同一 Service 进程的全部上述 worker。该预算不跨多个 Service 实例共享；名额限制的是 worker 进程，不是将 Jump 转发与目标 SSH 拆成独立连接数。

源码中仍保留旧 `SshNetH3cSshTransportFactory`、旧网络 `SshTerminalTransport` 及其历史处理代码，当前默认 DI、采集和网络终端分支不使用它们。它们是残留代码债务；“生产默认入口已统一”不等于“旧实现已经全部删除”。

## 驱动、认证和 Host Key

`tools/netmiko_worker.py` 集中保存下列明确映射，并检查当前 Netmiko `CLASS_MAPPER` 确实安装了对应驱动：

| 平台 | SSH | Telnet |
| --- | --- | --- |
| H3C Comware | `hp_comware` | `hp_comware_telnet` |
| Huawei VRP | `huawei_vrp` | `huawei_telnet` |
| Cisco IOS | `cisco_ios` | `cisco_ios_telnet` |
| Cisco IOS-XE | `cisco_xe` | `cisco_xe_telnet` |
| Cisco NX-OS | `cisco_nxos` | `cisco_nxos_telnet` |
| ZTE | `zte_zxros` | `zte_zxros_telnet` |
| Ruijie | `ruijie_os` | `ruijie_os_telnet` |
| Juniper | `juniper_junos` | `juniper_junos_telnet` |

厂商与 Cisco 平台从关联库存选择，未知或歧义映射拒绝。worker 不删除驱动后缀猜测协议，也不把未知厂商切换为其他厂商。内部显式 `DeviceType` 若与协议不符，同样拒绝；AUTO 不会把显式 SSH 驱动猜成 Telnet 驱动。正常 H3C 档案使用集中映射，ZTE 独立采集当前固定 SSH。

普通设备密码保存在设备档案中，按 profile ID 自动读取；生产未注册普通设备 DPAPI 凭据中心。密码通过 worker stdin JSON 传递，不通过命令行参数或日志。数据库中的密码字段受本机目录 ACL 保护，本身不是数据库加密。

worker 禁用用户 `known_hosts` 读写和交互确认，登录及 Netmiko 准备成功后才返回指纹。服务 Catalog 依据档案端点、修订和待确认状态记录首次信任或变化；认证失败不更新当前 SSH 指纹。目标设备、Jump Host 和 Linux 的凭据及指纹分别管理。Telnet 保持明文提示，不增加每次确认或重复输入密码的操作。

## 本批修改文件

- `src/NetConsole.Next.Service/H3cSsh.cs`：共享回退策略、实际协议、统一 session 尝试与释放、legacy 标志传递及 worker 名额等待、启动/终止同步。
- `src/NetConsole.Next.Service/BoundedCollectionScheduler.cs`：`IDeviceSession.Protocol` 默认成员，供连接测试报告实际协议；调度逻辑未重构。
- `src/NetConsole.Next.Service/H3cConnectionTestService.cs`：删除独立 Telnet 路径，统一使用 session，并正确报告最终协议。
- `src/NetConsole.Next.Service/TerminalSessionManager.cs`：Web Shell 使用相同策略和取消条件；首次发布 FAILED 时即持久化及公开稳定错误码，避免录制异步收尾期间返回异常文本。
- `tools/netmiko_worker.py`：原版 H3C Host Key 兼容参数与一次尝试、Jump 通道重建及清理。
- `src/NetConsole.Next.Service/NetmikoWorkerBudget.cs`：16 个共享进程名额、幂等 lease、实际退出后归还；先缓存重定向流，避免 worker 快速退出与获取管道的竞争。
- `src/NetConsole.Next.Service/NetmikoJumpHostProbe.cs`：复用共享名额，原 30 秒期限覆盖等待，探测结束终止进程，退出后归还。
- `tests/NetConsole.Next.Service.Tests/H3cConnectionPolicyTests.cs`、`Program.cs`、`TerminalCoreTests.cs`：连接策略、连接测试和既有终端回归。
- `tests/NetConsole.Next.Service.Tests/NetmikoWorkerBudgetTests.cs`：共享饱和、取消、Dispose 等待、活跃进程持有、重复连接/清理、启动/连接失败、快速退出和 probe 释放；使用真实本机合成子进程。
- `tests/netmiko_worker_test.py`：H3C 兼容作用范围、尝试上限和 Jump 资源释放回归。

## 验证结果与证据范围

| 验证 | 本批实际结果 | 能证明的范围 |
| --- | --- | --- |
| `py -3.13 -B tests/netmiko_worker_test.py` | **24/24 PASS** | 驱动、错误、交互及新增 H3C 兼容条件、Netmiko 包装异常、单次尝试、Jump 通道重建和释放；使用合成对象 |
| Service Tests Release 构建，`--no-restore` | **0 warnings / 0 errors** | 本批 C# 代码与测试可构建 |
| Service Tests `--h3c-connection-policy-tests` | **EXIT 0；6 groups PASS**；同入口既有 `TerminalCoreTests` 完成 | AUTO 两类可切换错误、多类禁止切换错误、显式协议、失败上限、取消、实际协议、单次密码读取与无重复命令；含隔离 Catalog |
| Service Tests `--netmiko-worker-budget-tests` | **EXIT 0；6 groups PASS，shared limit 16** | 饱和时 transport 与 probe 同时受限、取消不误释放、Dispose 后不启动、活动进程持有、一次启动、重复清理、启动失败、认证失败/EOF/取消、快速退出已写响应、probe 完成/失败释放 |
| 完整默认 Service harness，最终预算、批量库存 guard 及终端错误码修复后 | **EXIT 0**；根任务 session69905 | 包含连接策略 6 组、预算 6 组、批量连接 8/8、缓存 4/4、F10 3/3、Runtime 13/13、Concurrency 5/5、Raw task fact 13/13，以及迁移、权限、故障注入和释放回归 |
| `ssh_jump_host_e2e.py`，最终预算及终端错误码修复后 | **EXIT 0；`SITE_JUMP_HTTP_NETMIKO=PASS`；`SITE_JUMP_RESTART_DELETE=PASS`** | 实际 loopback SSH `direct-tcpip` 转发、HTTP/Netmiko 及重启删除路径；首次 FAILED 即断言稳定错误码，不放宽等待；不是实际现场 Jump Host 验收 |
| TerminalCore 确定性回归 | **PASS**；包含于策略定向入口及最终完整 harness | 用既有 BeforeIndexPublish hook 阻塞录制收尾，核验内存和 Catalog 在收尾前后均返回 FAILED 与 JUMP_HOST_CHANNEL_FAILED；原代码稳定失败，修复后通过 |
| 差异格式检查 | `git diff --check` 通过 | 本批源代码、测试及本文无差异格式错误 |

定向 C# 测试将 `TEMP` / `TMP` 指向 `D:\study\NetConsole-Next-Workspace\test-data`，只创建独立最小 Catalog fixture，结束后清理；使用合成设备地址和凭据。未复制旧 Site 或完整真实数据根。

预算测试复用测试程序集作为 JSON worker 子进程，不依赖外部 Python 安装，也不使用真实设备。它验证生产 C# 进程启动和释放路径；实际 CPython/Netmiko 协议及 Jump 转发仍由 Python 单元测试和根任务 E2E 覆盖。

已有 Windows 离线证据覆盖 CPython 3.13 x64、Netmiko 4.7、本地 wheel/hash/no-index 安装、隔离运行包搬迁和 loopback Telnet，以及此前的候选组合测试。这些既有包不能证明已包含本批连接修复；应以根任务随后生成的新候选及其证据为准。目标机 SCM 安装、升级、卸载和跨 Windows 验收仍待完成；当前 framework-dependent Service 需要 .NET 10 ASP.NET Core Runtime。

已有 `reports/netmiko-simulator-20261106.json` 记录 HCL AC1 `192.168.56.2`、AC2 `192.168.56.5` 和 SW `192.168.56.3` 的只读命令返回；这是前序批次证据，本次连接策略审计未重新登录这些模拟器。远端配置写入为 0。

## 保留问题

1. VLAN 实时 CLI 采集、完整 AP Group 证据、原版完整 AC 命令集与 CLI 诊断导出尚有缺口。专用批量连接入口已补齐，限定为独立 H3C 的只读版本命令测试，不扩大其他厂商的业务兼容范围。
2. Huawei、Cisco、Ruijie、Juniper 当前的驱动可选性不能扩写成这些厂商的全业务兼容。未验证型号与真实交换机、路由器、AC 均标记 `REAL_DEVICE_PENDING`。
3. 配置备份仅覆盖当前声明的独立 H3C 运行配置；原版启动配置、保存配置和其他厂商流程尚未完整迁移。本批未执行任何远端配置变更。
4. AUTO 目前共用一个档案端口和凭据；不提供原版独立 SSH/Telnet 参数。显式内部驱动与协议不符时直接失败。
5. 断线后的通用自动重连、退避和命令重放尚未实现；已有任务显式重试入口不能当作完整的自动恢复能力。
6. 真实 Linux、真实 Jump Host、双 AC HA 与现场切换恢复、正式离线安装和其他 Windows 环境仍待验收。

本批保留当前分支，仅本地提交；未执行 Push、Merge 或 Release。
