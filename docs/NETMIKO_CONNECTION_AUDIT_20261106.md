# Netmiko 连接盘点与架构验收

日期：2026-11-06。分支：`codex-A/netconsole-next-r3-dual-ac`。

网络设备 CLI 统一采用 **CPython + Netmiko**；本机连接架构收口完成，全部产品业务与实机兼容仍为 `PARTIAL / REAL_DEVICE_PENDING`。最终源码、离线候选与测试结果见 [统一架构验收](NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。本文以符号定位入口，避免后续删除旧代码使历史行号失效。

## 原版连接能力清单

只读参考仓库：`D:\study\NetConsole-Workspace\NetConsole`。未访问其真实业务数据根。

| 原版能力 | 已研究源码 | Next 继承与差异 |
| --- | --- | --- |
| Netmiko 建连、协议选择、自动认证、异常分类 | `src/netconsole/services/netmiko_connection.py`、`connection_manager.py` | 统一 Worker 与轻量 session；AUTO 备用协议、超时、取消及中文原因码集中管理 |
| H3C / ZTE SSH 与 Telnet 驱动 | `netmiko_connection.py` 驱动常量及连接参数 | 使用相应明确驱动；不删除后缀、不猜测厂商、不静默回退错误平台 |
| H3C 旧 Host Key 协商兼容 | `_connect_with_compatibility`、`_legacy_ssh_rsa_params` | 仅显式启用且为已识别的 Comware Host Key 协商失败时一次兼容尝试 |
| 命令、分页、回显和编码 | `safe_send_command`、`safe_send_command_with_paging`、各采集服务 | Next 使用 Netmiko 驱动准备和 `send_command`，不迁移手工分页循环；业务 parser 留在服务 |
| 密码保存、自动读取、SSH 指纹与中转 | `device_credential_store.py`、`host_key_trust_service.py`、`host_key_manager.py`、`ssh_tunnel.py` | 按设备/Profile 或 Site 读取密码；认证后自动首次信任/轮换；普通凭据不采用 DPAPI 中心 |
| 连接恢复 | `run_netmiko_with_retry`、`mesh_link_resident_polling_service.py`、`online_mr_collector.py`、`ssh_resilient_worker.py` | 有界重连两次、可取消退避；只在发送前明确断线时恢复当前只读命令，不重放任意 callback |
| AC / FIT-AP / AP Group、配置生命周期、诊断 | `h3c_ac_collect_service.py`、`services/ac/`、`config_lifecycle_service.py`、`diagnostic_download_service.py` | 已补齐 AC/AP 必选与可选资源命令、运行/保存配置和启动指向备份、只读全机诊断；稳定生产 GroupEvidence、启动文件正文/恢复及真实固件互操作仍待 |

原版允许 SSH 与 Telnet 各自保存端口和凭据；Next 当前 AUTO 使用同一档案端点，不宣称两者参数模型等同。原版驱动存在和历史设备经验不等于 Next 型号兼容证据。

## Next 连接入口清单

统一链路：`Program.cs` 注册 `H3cSshDeviceSessionFactory` → `NetmikoWorkerH3cSshTransportFactory` → `tools/netmiko_worker.py` → `netmiko.ConnectHandler`。`.NET` 管理身份、Site、凭据传递、命令计划、取消、授权、Host Key、Raw/Facts 和 SQLite；Worker 只执行连接与命令交互。

| 业务入口 | 实际连接或读取路径 | 当前范围 |
| --- | --- | --- |
| 新建设备、单台连接测试 | `DevicesView` → `H3cConnectionTestService` → 统一 session | 保存后自动测试、后续自动读取密码；SSH 信息识别目前为 H3C，Telnet 只验证登录 |
| 专用批量连接测试 | `DeviceConnectionTestService` → `H3cBatchTaskService` → 统一调度器 | 精确库存 UUID，1–64 台独立 H3C，固定只读版本命令；FIT-AP 拒绝独立登录 |
| 设备详情与 Facts 刷新 | Catalog 已存事实 API | 刷新本身不发起 CLI；采集任务完成后读取新事实 |
| H3C 信息、接口、VLAN、LLDP、光模块 | 命令计划 → `BoundedCollectionScheduler` → 统一 session | VLAN 已补齐 `display vlan all` detail parser、Raw 发布和整表快照；实机型号待验收 |
| ZTE 信息、接口、LLDP、光模块 | `ZteCollectionService` → 相同 Netmiko transport | ZXROS 五条命令及独立 Profile；每条命令、重连和发布前复核 actor/Site/端点 |
| AC 与 AC 下 FIT-AP | `H3cAcResources` 固定命令计划 → 同一调度器；`WirelessReadModel` 读取 AC Facts | AP 列表/地址/射频、六项可选资源及 CPU/内存/制造信息；可选项保留命令证据，不捏造结构化解析；FIT-AP 经 AC 管理 |
| AP Group 视图 | 同一 AC 的 AP 事实、来源修订和投影 | 组名为来源 AC 限定的 CANDIDATE，厂商身份键为空；独立生产 GroupEvidence 仍待 |
| 配置采集与备份 | `ConfigBackupService` 选择 RUNNING/SAVED/STARTUP_INFO 固定命令 → 统一调度器 | 独立 H3C 三类分开编号、历史/差异/下载；STARTUP_INFO 仅文件指向，未实现启动文件正文和恢复 |
| 设备诊断 | ICMP/TCP/DNS、已存 LLDP Facts；`H3cDiagnosticCapture` → 同一 Netmiko session | `display diagnostic-information` 540s/4MiB，只对已验证措辞回答一次 n；不重放。封存 TXT/ZIP 下载不重新采集 |
| 网络 Web Shell | `TerminalSessionManager` → `NetmikoTerminalTransport` → Worker 交互通道 | 复用设备密码、厂商、协议、指纹与 Site Jump；SSH 支持远端 PTY resize，Telnet 不支持远端 resize |
| Linux 服务器 | 独立 `LinuxSshTerminalTransport` / SSH.NET ShellStream | 保留独立 inventory、认证、WebSocket 实时终端及生命周期，不强制使用网络 Site Jump |
| Site Jump Host | Paramiko `SSHClient` / `direct-tcpip` 转发或 Jump probe | 只用于必要中转；目标网络设备仍由 Netmiko 登录。Telnet/Ping/SNMP/UDP 不套用该中转 |

## 重复实现与不一致项处理

- 已删除旧网络 `SshNetH3cSshTransportFactory`、`SshNetH3cSshTransport`、`SshTerminalTransport`，旧回显/分页收集器、Host Key 算法策略及其专用 wire harness。
- 已删除旧流式文本解码器；保留必要的编码配置和业务 `CliTranscriptParser`，后者只处理已完成回显的业务解析、残留和完整性，不负责网络读取或分页交互。
- 已删除只测试旧 Exec 路径的 `ssh_protocol_sim.py` 与不匹配的旧 Paramiko requirements。CI 改为 CPython 3.13、锁定 Netmiko/Paramiko、实际 Worker/reconnect/batch/Jump/Linux 回环门禁。
- 生产普通网络 CLI 唯一建连库为 Netmiko。C# `SshClient` 仅由 Linux 实现持有；Python `SSHClient` 仅用于 Jump 转发和探测。串口与 TCP 网络诊断保留各自用途。
- 每个 Service 实例的全部 Worker/Jump probe 共用 16 个可取消名额，实际进程退出才归还；没有增加 SSH 连接池。
- 连接恢复耗尽后稳定分类为不可达/断线；显式 Jump 转发拒绝不重试。Jump 配置并发变化仅结束当前连接，不冒充数据库持久化故障。

## 驱动映射

映射集中于 `tools/netmiko_worker.py` 的 `DRIVER_ALIASES` 和 `resolve_driver`，同时检查 Netmiko `CLASS_MAPPER`。Cisco 必须有明确平台，显式驱动必须匹配协议。未知厂商/平台或未安装驱动拒绝，不静默回退。

| 平台 | SSH | Telnet |
| --- | --- | --- |
| H3C Comware | `hp_comware` | `hp_comware_telnet` |
| Huawei VRP | `huawei_vrp` | `huawei_telnet` |
| Cisco IOS | `cisco_ios` | `cisco_ios_telnet` |
| Cisco IOS-XE | `cisco_xe` | `cisco_xe_telnet` |
| Cisco NX-OS | `cisco_nxos` | `cisco_nxos_telnet` |
| ZTE ZXROS | `zte_zxros` | `zte_zxros_telnet` |
| Ruijie | `ruijie_os` | `ruijie_os_telnet` |
| Juniper Junos | `juniper_junos` | `juniper_junos_telnet` |

这些映射证明使用支持的驱动，不代表这些厂商都已具备信息、接口、VLAN、LLDP、AC、光模块、配置备份全套业务或实机型号兼容。

## 认证、恢复和验收边界

设备密码按 Profile 读取，Jump 密码按 Site 读取；留空更新保留旧值，明确清除才删除。普通 API、日志、任务快照、Raw 和命令行不输出密码。当前 SQLite 为本机 ACL 保护的明文字段，不宣称数据库加密。SSH 密码和 Host Key 分别管理；首次及变化指纹在认证成功后记录，不弹确认窗口。Telnet 提示明文传输，不强制额外操作。

有界断线恢复、共享预算、精确只读命令和授权/Host Key/CAS 边界见 [重连验收](NETMIKO_RECONNECT_ACCEPTANCE_20261106.md)。Windows 内置 CPython 运行与系统 Python 隔离的准确版本候选见 [统一架构验收](NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)；本机候选运行不替代目标机 SCM、安装/升级/卸载或远端 CI。

已有 HCL AC1/AC2/SW Netmiko 只读登录及命令证据位于 Workspace `reports/netmiko-simulator-20261106.json`，设备配置写入为 0。它不证明双 AC HA、切换恢复、AP 身份或实时归属。硬件、真实 Linux/堡垒机、未验证型号均 `REAL_DEVICE_PENDING`。Web 仍限 loopback，跨机 TLS、原生 125% 缩放、完整工作台与无线权威生命周期继续待完成。保留当前分支，不 Push、Merge、Release。
