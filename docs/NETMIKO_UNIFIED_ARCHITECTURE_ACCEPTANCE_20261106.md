# 网络设备统一 Netmiko 架构验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。

功能提交：`9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，包含前序 VLAN `170bf81`、任务诊断 `34b524d` 与有界重连 `4ee1cb7`。本机连接架构收口及下述离线候选运行验收通过；全产品仍为 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，仅本地提交，不 Push、Merge、Release。

## 架构及原版继承

原版源码只读参考为 `D:\study\NetConsole-Workspace\NetConsole`。已研究 `netmiko_connection.py`、`connection_manager.py`、设备凭据与 Host Key 服务、`ssh_tunnel.py`、AC/配置/诊断及持续采集恢复实现。原版能力、Next 全部现有入口和逐项差异见 [连接盘点](NETMIKO_CONNECTION_AUDIT_20261106.md)；策略、业务入口和未迁移能力见 [策略对照](NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md)。没有访问原版真实业务数据根。

网络设备 CLI 统一链路为 `H3cSshDeviceSessionFactory` → `NetmikoWorkerH3cSshTransportFactory` → CPython `tools/netmiko_worker.py` → Netmiko `ConnectHandler`。H3C/ZTE 采集、单台和批量连接测试、运行配置备份与网络 Web Shell 均共用此连接体系。FIT-AP 保持 AC 管理关系，不建立独立 CLI 登录。

Netmiko 负责 SSH/Telnet 连接、认证、驱动准备、提示符、分页、命令与回显。Next 继续管理设备 UUID、Site、分组、H3C Capability、Command Profile、业务命令选择与解析、任务、SQLite、Raw/Facts/历史和 AC/AP 关系。Worker 是进程隔离的轻量连接封装，不提供业务 repository 或连接池。

已删除旧网络 SSH.NET factory/transport、旧网络终端 `SshTerminalTransport`、自定义回显与分页收集器、旧 Host Key 算法策略、流式解码器及专用 wire harness。保留的 `CliTranscriptParser` 处理完成回显的业务解析和完整性，不参与网络读取。源码审计确认 C# `SshClient` 仅在独立 Linux 终端；Python `SSHClient` 仅在 Jump 转发/探测。目标网络设备始终通过 Netmiko 登录。

Linux 服务器按独立需求保留 SSH.NET ShellStream 与 inventory；串口、ICMP/TCP/DNS 诊断维持各自用途。Site Jump Host 只建立按需 `direct-tcpip` 转发，不用于 Telnet/Ping/SNMP/UDP。详情刷新读取已存事实，任务诊断下载读取已封存 Raw；这两者不额外建连。

## 驱动、密码及指纹

驱动由 Worker 的 `DRIVER_ALIASES` / `resolve_driver` 集中选择，并核验已安装 Netmiko `CLASS_MAPPER`：

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

Cisco 要求明确平台；未知厂商/平台、未安装驱动及显式驱动与协议不符均拒绝。不通过删除后缀推导驱动，不静默替换厂商。映射不代表所有设备型号或这些厂商的全部结构化业务已经兼容。Next AUTO 当前共用一套端点与凭据，尚未具备原版分别保存 SSH/Telnet 参数的完整模型。

普通设备密码按设备/Profile 自动读取，Jump 密码按 Site 读取，Linux 密码按服务器读取；更新留空保留，明确清除才删除。密码经本机 stdin 管道传给 Worker，不出现在普通 API、任务快照、Raw、命令行或日志。密码字段是受本机 ACL 保护的 SQLite 明文，不建设普通 DPAPI 凭据中心。SSH 密码与指纹分开保存。

首次与变化的 Host Key 均在成功认证后自动记录/轮换，不弹确认窗口；目标、Jump、Linux 指纹分别管理。认证失败不覆盖有效指纹；提交前检查配置修订/CAS，旧会话结果不能覆盖已更新配置。自动信任不能作为独立设备身份验证。Telnet 保留明文传输提示，不增加每次确认。

## 连接、并发及恢复

所有网络 Worker 和 Jump probe 共用每 Service 实例 16 个可取消名额，进程实际退出后归还；不创建 SSH 连接池。已有调度并发和队列边界继续适用。统一处理连接/登录/命令超时、取消、输出上限、连接释放与中文错误分类。

一个逻辑会话最多两次额外重连，250/1000 ms 退避，共享任务 deadline、取消和恢复预算。只有发送前已确认断线的当前厂商/驱动精确只读命令允许恢复；已成功命令不重放，发送后断线、部分输出、ReadTimeout、Worker EOF/管道异常与交互 Shell 不自动重放。每条命令和每次重连前复核当前 actor/Site/Profile/端点。详见 [重连专项](NETMIKO_RECONNECT_ACCEPTANCE_20261106.md)。

本次补齐稳定异常边界：登录连接丢失为 `UNREACHABLE`，终端断线为 `TRANSPORT_DISCONNECTED`；显式中转拒绝为 `JUMP_HOST_FORWARD_DENIED`，只尝试一次。Jump 指纹提交遇到配置并发变化为 `JUMP_HOST_CONFIGURATION_CHANGED`，仅结束该连接，不把正常并发编辑当作 Catalog 持久化故障，不继续发命令。

## 当前提交验证

| 门禁 | 结果与证据范围 |
| --- | --- |
| Service/Tests Release build | PASS，0 warnings / 0 errors；`--no-restore -m:1` |
| 默认完整 Service harness | session31025，EXIT0；重连172/172、策略6组、共享预算6组/16名额、ZTE边界12/12、任务Site13/13、批量连接8/8、交互scope6/6、H3C事实scope8/8、VLAN parser6/6、事实发布14/14、诊断89/89、并发5/5及既有无线/迁移/崩溃/SQLite/Raw/恢复回归 |
| 网络终端契约 | `terminal_ui_contract.py`，3 checks PASS |
| 实际浏览器网络终端 E2E | session87828，EXIT0；Vue→WebSocket→内置连接体系→loopback SSH/Telnet，AUTO、Unicode、PTY resize、重连、分屏/搜索/主题、COM、ticket拒绝、历史/回放/JSONL导出 PASS；SSH初始61×22、resize90×28 |
| Worker与HTTP重连 | 前序同批29/29与HTTP13 checks PASS；合成对象/loopback证据，当前准确版本候选另验证真实内置Worker恢复 |
| 最终 Jump 回环 | session65279，EXIT0；`SITE_JUMP_HTTP_NETMIKO` / `SITE_JUMP_RESTART_DELETE` PASS，包含转发拒绝只尝试一次的新断言；实际loopback direct-tcpip，两Site、采集/测试/终端、取消释放、指纹/错误与重启删除 |
| 离线候选构建 | session2553，EXIT0；当前Vue构建2084模块，复制前后隔离driver/Telnet/JSON smoke PASS；Vite体积提示仍存在 |
| 准确候选运行 | session16200，EXIT0；以下7项全PASS |
| 源码与差异 | 网络CLI sole `ConnectHandler`；仅Linux持有C# `SshClient`；格式检查通过 |

候选：`D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`。EXE ProductVersion 已逐字核验为 `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`。复用 Worker 未变化的 `build/netmiko-reconnect-final-20261106`，内置 CPython3.13、Netmiko4.7.0、Paramiko4.0.0及18个固定版本/hash wheel；本地 `--no-index --require-hashes` 安装。

候选在 Production 环境、独立 cwd/隔离数据根中运行，PATH 仅 System32，清除 Python/Worker 覆盖。测试驱动的 Python 只提供客户端和合成SSH目标，候选使用自身内置运行时：

- `OFFLINE_BUNDLED_RUNTIME=PASS`：内置版本与依赖。
- `OFFLINE_STATIC_UI=PASS`：HTTP资源hash，Linux/Jump/终端/设备批量与备份/VLAN/任务诊断入口。
- `OFFLINE_NETMIKO_CONNECTION=PASS`：自动登录loopback SSH及只读命令。
- `OFFLINE_TASK_DIAGNOSTICS=PASS`：封存TXT/ZIP内容与hash，无重新采集。
- `OFFLINE_NETMIKO_RECONNECT=PASS`：发送前断线恢复、Raw顺序及已完成命令不重放。
- `OFFLINE_LINUX_WEBSOCKET=PASS`：自动读密码，ANSI/Unicode、原始控制键、resize与关闭。
- `OFFLINE_CANDIDATE=PASS`：上述隔离候选运行条件。

结构化报告：Workspace `reports/netmiko-final-9fc499f-20261106.json`。构建与运行方法见 [离线部署](NETMIKO_OFFLINE_DEPLOYMENT.md)。候选为framework-dependent，目标机仍需.NET10 ASP.NET Core Runtime；没有安装SCM，不代表目标机安装、升级或卸载通过。

候选和最终Jump测试结束后，Next Service/Python残留0，`offline-candidate-*`临时目录0；用户Vite PID90348及`127.0.0.1:18792`监听保留。

CI源码已改用CPython3.13和统一固定依赖，退役旧Exec-only模拟器，加入Worker、重连、批量、Jump、Linux门禁。本任务禁止Push，未触发或声称远端CI成功。

## 验收边界与下一步

HCL AC1/AC2/SW 前序 Netmiko只读登录与命令证据为 Workspace `reports/netmiko-simulator-20261106.json`。FIT-AP保持AC管理；这些证据不证明物理硬件兼容、双AC HA、切换恢复、AP身份或实时归属。本批没有设备配置变更，没有访问/复制旧NetConsole真实数据根；只使用Next Workspace隔离fixture和合成目标，用户Vite PID90348保留。

完整AC命令和稳定生产GroupEvidence、saved/startup配置生命周期、一次性`display diagnostic-information`命令、其他厂商结构化业务、正式candidate确认/拒绝与identity/replacement/Domain/group权威生命周期、剩余工作台仍有缺口。任务封存诊断下载已实现，不能写成原版一次性全机诊断已经迁移。

真实交换机/路由器/AC/独立AP/防火墙/工业交换机、真实Linux/Jump、目标机安装/SCM/升级卸载、Windows原生125%与跨机TLS均待验收。Web继续loopback，整体保持PARTIAL / REAL_DEVICE_PENDING。
