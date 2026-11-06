# 实现矩阵

## 2026-11-07 当前实现矩阵：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

| 任务 | 当前交付与实际验证 | 保留边界 |
| --- | --- | --- |
| FD-P1-37 AC资源 | 原版必需3/可选6/补充概览3、逐命令Raw/Facts及当前/历史UI；backend3/3、HTTP21、Browser7、离线AC_RESOURCES PASS | version/device沿用已有投影；可选正文为非结构化证据；组名按来源AC保持CANDIDATE |
| FD-P2-06 三类备份 | RUNNING/SAVED/STARTUP_INFO独立版本、同类diff、Raw前及下载防御脱敏（含完整/截断PEM）；backend9/9、HTTP28、Browser9、离线CONFIGURATION_KINDS PASS | STARTUP_INFO只是启动文件指向；startup正文/restore未实现 |
| FD-P2-07 全机诊断 | 独立display diagnostic-information；任务600000ms、Worker共享540s期限、原始回显4MiB、仅精确已知问题回一次n；helper30/30、evidence96/96、HTTP24、Browser9、离线FULL_DIAGNOSTIC PASS | 未知问题不回答、结果未知不重放；仅合成提示证据，诊断正文不发布普通Facts |
| 备份下载no-store | ef57213为配置下载补充Cache-Control: no-store；当前版本离线检查含缓存头和脱敏 | 不推定历史浏览器缓存已清除；下载不重连/重采集 |

全部继续复用统一CPython/Netmiko、Profile自动凭据、任务/Site授权及sealed Raw；FIT-AP由AC管理。当前名称观察的SourceGroupKey/GroupIdentityKey均为null，两个AC同名组不合并，已有GroupEvidence fixture不代表生产稳定键。

功能提交 `e93b382` 的完整后端 harness session `23456` EXIT 0 PASS，包含配置备份 9/9、全机诊断 helper 30/30、诊断证据 96/96、AC 资源 3/3 及既有回归；同提交 Worker 40/40、前端 14 files / 140 tests 和 typecheck PASS。`ef57213` 的 no-store 修复后重跑 Service Release 构建、配置 HTTP 28 项与完整离线十项门禁，未重复完整 harness、Worker 或前端单测。浏览器为真实 Chrome/源码 UI/API mock，HTTP 为实际 Service → CPython/Netmiko → 合成回环 SSH，均不替代真实型号验收。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-cache-fix-20261107`，ProductVersion `1.0.0+ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。新 bundle `build/netmiko-inheritance-final-20261106` 包含 18 个 pinned/hash wheels（CPython 3.13 / Netmiko 4.7 / Paramiko 4）；bundle build `13581`、candidate build `64183` 均 EXIT 0，Vue 2084 modules 与复制前后 driver/Telnet smoke PASS。离线运行 `71491` EXIT 0，十项标记全部 PASS。运行环境为 Production、隔离 cwd/数据、PATH 仅 System32，无 Python/worker 覆盖；未使用旧 NetConsole 生产数据。候选本机隔离运行不代表目标机安装，目标机仍需 .NET 10 ASP.NET Core Runtime。

剩余：稳定生产 `GroupEvidence`、startup 文件正文/restore、其他固件与厂商的结构化业务、candidate 确认/拒绝、identity 合并/拆分、replacement 写入/撤销/revision 合同、可靠终止与 Domain/group 生命周期、双 AC HA/实时归属、剩余工作台、Windows 原生 125%、跨机 Web/TLS 与目标机安装/SCM/升级/卸载；不从缺席推断 `ENDED`。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。

## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 整体 **PARTIAL / REAL_DEVICE_PENDING**。最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

| 能力 | 当前实现与证据 | 当前阶段 | 仍需证据/工作 |
| --- | --- | --- | --- |
| 多设备/多命令任务 | bounded scheduler、设备互斥、命令级 Raw、完整 manifest 成功门禁 | PASS（隔离） | 实际业务批量采集 |
| 重试/取消/恢复 | 直接按 ID 查询，当前 Site 与 SUPERADMIN 复核 | PASS（隔离） | 实机重启/恢复 |
| 网络 CLI 统一连接 | CPython + Netmiko；H3C/ZTE 采集、连接测试、Web Shell 共用入口 | PASS（本机 + HCL） | 真实硬件/型号兼容 |
| 驱动选择与AUTO/legacy | 明确厂商/平台/SSH/Telnet映射，未知映射拒绝；共用轻量协商策略，限定一次Telnet或H3C legacy尝试 | 策略6组、worker29/29、最终完整Service harness31025及Jump65279两项PASS | 映射存在不等于型号已验证，协商范围与下行有界发送前恢复分别验收；不允许任意命令重放 |
| Netmiko有界发送前恢复 | 4ee1cb7；最多2次额外连接、250ms/1000ms退避，共享原期限/取消/16 worker槽；每次连接实时授权与认证后Host Key更新 | C#172/172、worker29/29、HTTP13、ZTE12/12、scope13/13 PASS | 仅发送前已知断线及驱动精确只读allowlist；发送后未知结果/部分回显/交互Shell不重放；真实设备待验收 |
| 重复网络transport退役 / CI | 9fc499f删除旧SSH.NET网络设备transport/decoder/测试，修复连接/Jump CAS分类；Linux保留；CI转CPython3.13真实Netmiko | COMPLETED_LOCAL；最终harness31025 EXIT0 | CI仅workflow与本机等价门禁，未执行远端CI；精确SHA候选七项PASS |
| Netmiko进程资源预算 | 每Service实例共享16个CPython worker槽，覆盖采集/单台测试/网络WebShell/Jump探测；可取消等待，实际退出才归还 | 预算6组（真实合成子进程）、最终完整Service harness及Jump两项PASS | 不跨Service实例；不是SSH连接池；真实设备并发容量待验收 |
| 专用批量连接API/UI | 明确选择1–64库存UUID，先整批可见性、后兼容/精确Profile关联预检；复用任务计划/调度器/Netmiko，展示命令报告与取消 | 8320e2f批次定向8/8、Vue12 files/82 tests/typecheck；当前完整harness及Vue14 files/124 tests PASS、Chrome mock4组、guard后实际HTTP→Netmiko14 checks PASS | 限SUPERADMIN及启用的独立H3C，只读display version；FIT-AP仍经AC管理，实机兼容待验收 |
| 设备凭据/快速添加 | Profile 密码自动读取；库存/Profile 原子提交；普通 DTO 不回显；ACL | PASS（API/UI） | SQLite 字段明文；不宣称数据库加密 |
| SSH 自动信任/轮换 | 认证后首次/变化自动更新，旧 STRICT 采用自动行为，配置并发校验 | PASS（本机/回环） | 实际设备互操作 |
| Site Jump Host | Site 独立密码/指纹，Netmiko/Paramiko direct-tcpip，Telnet 不套用 | PASS（回环转发 + Chrome Settings） | 实际堡垒机兼容 |
| 网络 Web Shell | 同一 Netmiko worker，resize、重连、取消、录制/回放 | PASS（完整 E2E） | 真实设备兼容 |
| Linux 服务器终端 | 独立 inventory、SSH.NET ShellStream、WS、中文/ANSI/resize/重连 | PASS（HTTP/WS + Edge） | 实际 Linux；不继承 Site Jump 策略 |
| ZTE 凭据/API/UI | 新增、空白保留、明确清除、自动采集凭据 | PASS（本机） | REAL_DEVICE_PENDING |
| Windows 离线组合包 | 9fc499f当前Vue + 内置CPython3.13/Netmiko4.7/Paramiko4；Production/隔离数据/PATH仅System32；七项门禁含reconnect与diagnostics | COMPLETED_LOCAL，构建2553/离线16200 EXIT0 | 目标机需.NET10 ASP.NET Core Runtime；安装/SCM/升级/卸载待验收，候选不是正式发布；最终进程/临时目录残留0 |
| AP identity / 双 AC | exact/唯一MAC受控关联、冲突Observation、Current/Recent10；F10新旧身份/运行态/来源隔离3/3 PASS | PARTIAL | candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销/revision并发合同及可靠终止证据仍待；HA另待验收 |
| FIT-AP | 依 AC 管理与采集，不独立连接 | 当前架构约束 | live ownership/业务证据 |
| Raw→Facts→Current | 单/多命令封存、完整性与 collection provenance | PASS（隔离） | 实时多 AC 业务端到端 |
| Site 授权/撤权 | 当前 claims 与 captured scope 交集，文件/终端/任务复核 | PASS（本机回归） | 跨机 Web/TLS |
| 设备管理 | Site/分组/CSV/批量、快速添加、连接测试及 ZTE 入口 | PASS（本机 API/UI） | 实际库存兼容 |
| 诊断/备份/拓扑 | 库存UUID→明确Profile备份入口，保序/重复次数有界diff；6/6、Web65、Chrome6、HTTP→Netmiko19 PASS；诊断/LLDP既有证据保留 | PASS（隔离API/UI/回环） | 当前备份只支持独立H3C；现场与完整实时业务验收仍待 |
| H3C VLAN采集 | `170bf81`：display vlan all→统一Netmiko→Raw/Facts整表Current/history及UI；partial/unsupported/empty独立状态 | parser6/6、publication14/14、HTTP合成SSH25 checks、Chrome mock6场景 PASS | 仅Comware detail格式，其他厂商/真实型号待验证；不推断PVID、不将规划数据当作实采 |
| CLI诊断下载 | `34b524d`：任务封存回显manifest、单条TXT/ZIP、配置排除、双hash/归属核验、64KiB撤权与取消、前端迟到隔离 | 后端89/89、HTTP合成SSH21 checks、TasksView20/20、Chrome mock8/8、完整Vue14 files/124 tests PASS | 不增加采集/文件缓存；真实设备与全部AC命令仍待 |
| 完整多厂商业务 | 统一显式Netmiko driver映射，H3C/ZTE既有结构化业务 | PARTIAL | 原版完整AC命令/稳定组证据、保存/启动配置及其他厂商完整业务仍待，driver映射不代表全业务兼容 |
| 模块治理 | schema v17、SUPERADMIN Settings、核心保护、重启保留 | PASS（隔离 API/UI） | 剩余完整模块工作台 |
| R3 持久化 / manifest | schema18/19事实/manifest；schema24来源Group可重建投影；候选缓存按逐来源时间水位收敛4/4 PASS | PASS（隔离API/UI） | cache退役不删除Raw/Facts/历史Observation，不是完整Domain/group生命周期；live多AC/HA仍待 |
| UI / Web | 1920/2K、网络终端、Linux、Jump 页面本机浏览器通过 | PARTIAL | Windows 原生 125%、跨机 Web/TLS |

当前 HCL AC1/AC2/SW 只读 Netmiko 连接/命令 PASS；旧 SW Exec/FIT-AP 独立登录和 Mutex 争用不再是当前阻断。未执行设备配置变更；实机状态 `REAL_DEVICE_PENDING`。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。 有界发送前恢复、旧网络 transport 退役与精确 SHA 离线候选已通过本机门禁。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 集成验证（历史）

以下保留原提交与候选证据，仅适用于该历史时点。

最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` 的Release Service/Tests构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。本轮策略6组、预算6组与批量8/8分别PASS，批量已纳入默认完整harness；Web typecheck及12 files/82 tests、guard后批量HTTP14/14 PASS。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。文档以所属提交为准；证据见对应20261106验收文档。
