# 全天候自主开发最终报告

## 2026-11-07 当前交付：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载补充 `Cache-Control: no-store`，当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

| 任务 | 当前交付与实际验证 | 保留边界 |
| --- | --- | --- |
| FD-P1-37 AC资源 | 原版必需3/可选6/补充概览3、逐命令Raw/Facts及当前/历史UI；backend3/3、HTTP21、Browser7、离线AC_RESOURCES PASS | version/device沿用已有投影；可选正文为非结构化证据；组名按来源AC保持CANDIDATE |
| FD-P2-06 三类备份 | RUNNING/SAVED/STARTUP_INFO独立版本、同类diff、Raw前及下载防御脱敏（含完整/截断PEM）；backend9/9、HTTP28、Browser9、离线CONFIGURATION_KINDS PASS | STARTUP_INFO只是启动文件指向；startup正文/restore未实现 |
| FD-P2-07 全机诊断 | 独立display diagnostic-information；任务600000ms、Worker共享540s期限、原始回显4MiB、仅精确已知问题回一次n；helper30/30、evidence96/96、HTTP24、Browser9、离线FULL_DIAGNOSTIC PASS | 未知问题不回答、结果未知不重放；仅合成提示证据，诊断正文不发布普通Facts |
| 备份下载no-store | ef57213为配置下载补充Cache-Control: no-store；当前版本离线检查含缓存头和脱敏 | 不推定历史浏览器缓存已清除；下载不重连/重采集 |

功能提交 `e93b382` 的完整后端 harness session `23456` EXIT 0 PASS，包含配置备份 9/9、全机诊断 helper 30/30、诊断证据 96/96、AC 资源 3/3 及既有回归；同提交 Worker 40/40、前端 14 files / 140 tests 和 typecheck PASS。`ef57213` 的 no-store 修复后重跑 Service Release 构建、配置 HTTP 28 项与完整离线十项门禁，未重复完整 harness、Worker 或前端单测。浏览器为真实 Chrome/源码 UI/API mock，HTTP 为实际 Service → CPython/Netmiko → 合成回环 SSH，均不替代真实型号验收。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-cache-fix-20261107`，ProductVersion `1.0.0+ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。新 bundle `build/netmiko-inheritance-final-20261106` 包含 18 个 pinned/hash wheels（CPython 3.13 / Netmiko 4.7 / Paramiko 4）；bundle build `13581`、candidate build `64183` 均 EXIT 0，Vue 2084 modules 与复制前后 driver/Telnet smoke PASS。离线运行 `71491` EXIT 0，十项标记全部 PASS。运行环境为 Production、隔离 cwd/数据、PATH 仅 System32，无 Python/worker 覆盖；未使用旧 NetConsole 生产数据。候选本机隔离运行不代表目标机安装，目标机仍需 .NET 10 ASP.NET Core Runtime。

资源核对由主 Agent 完成：Next Workspace 相关 Service/Python/py 进程 0，`test-data` 顶层 `offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` Listen 保留。单个最小 SQLite fixture 按规则保留，不能写全部测试目录 0，确切路径见 [最终交接](FINAL_HANDOVER.md)。未访问/复制旧 NetConsole 数据根，真实设备配置写入 0。

剩余：稳定生产 `GroupEvidence`、startup 文件正文/restore、其他固件与厂商的结构化业务、candidate 确认/拒绝、identity 合并/拆分、replacement 写入/撤销/revision 合同、可靠终止与 Domain/group 生命周期、双 AC HA/实时归属、剩余工作台、Windows 原生 125%、跨机 Web/TLS 与目标机安装/SCM/升级/卸载；不从缺席推断 `ENDED`。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。

## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

## 2026-11-06 当前检查点：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。

VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

仍待正式 candidate/identity/replacement 写流程、关系终止与 Domain/group 生命周期，双 AC HA/实时归属、完整 AC 命令与稳定 GroupEvidence、保存/启动配置、其他厂商结构化业务、剩余工作台、Windows 原生 125%、跨机 Web/TLS、目标机安装与实际设备兼容。

VLAN 仅覆盖已测试 Comware detail 格式；诊断只读取已有封存输出，排除配置命令。HTTP 合成 SSH、隔离 fixture 和 Chrome API mock 各守原证据范围，均不替代真实设备兼容；整体 **PARTIAL / REAL_DEVICE_PENDING**。有界发送前恢复已验收，具体恢复限制见下方 FD-P1-35；不把该证据扩展为任意命令重放。未写设备配置、未访问旧 NetConsole 数据根；保留当前分支，不 Push、Merge、Release。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

以下 8320e2f 及更早记录保留当时提交、测试数量与候选范围；其中“当前/最终”仅指历史时点。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 交付与边界（历史）

整体 **PARTIAL**。Branch：`codex-A/netconsole-next-r3-dual-ac`。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。保留当前分支，不 Push、Merge、Release。

网络设备CLI采集、连接测试与Web Shell统一CPython + Netmiko；显式SSH/Telnet驱动、Profile密码自动读取、认证后Host Key自动信任/轮换及原子onboarding已有本机验收。普通密码是受ACL保护的SQLite明文字段，无普通设备DPAPI中心。Linux仍采用独立SSH.NET ShellStream；Site Jump实际回环转发与Settings页、ZTE凭据UI已有证据。AC1/AC2/SW经Netmiko自动认证及只读命令PASS；FIT-AP由AC管理，旧SW Exec/独立FIT-AP登录结论只作历史。

最新本机增量：`fda2a68` AP Group来源身份/schema24；`2696390` FULL授权投影内按来源水位收敛候选缓存；`764d764` F10 conservative更换隔离；`e12d062` 设备UUID→明确Profile配置备份入口与保序/重复次数有界diff；`8320e2f` Netmiko策略、共享进程预算、专用批量连接及Terminal reason修复。缓存退役不表示业务ENDED；F10验证identity/runtime/离线输出/来源隔离，不构建正式replacement link，也未产生实时Raw分段完整性证据。

Group合同/Projection PASS、Runtime13/13、cache4/4、F10 3/3 PASS。配置备份独立6/6、Chrome源码UI+API mock6场景、实际HTTP Service→CPython/Netmiko→合成回环SSH备份19 checks PASS。完整Web12 files/82 tests/typecheck PASS，最终Release Service/Tests构建0 warnings / 0 errors。浏览器mock/离线fixture/合成SSH均有各自范围，不代表真实型号兼容。

本轮Netmiko轻量AUTO/legacy策略6组、worker24/24、Service实例共享worker上限16的预算6组PASS。专用批量连接API/UI已补齐：明确库存UUID及精确Profile、整批可见性优先、复用既有任务计划/调度器/Netmiko，固定只读display version；定向8/8、Chrome mock4组、guard后实际HTTP→Netmiko→合成回环SSH14 checks PASS。仅SUPERADMIN与启用的独立H3C，FIT-AP仍经AC管理。Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness；资源预算不构成SSH连接池或第二套调度器。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，EXE ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS；STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。历史a3954a5候选仅保留原范围；当前8320e2f候选通过本机隔离验收，候选不是正式发布。

尚待正式candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销与revision并发合同，以及可靠终止证据与Domain/group生命周期；不从缺席推断ENDED。VLAN实时CLI采集、完整多厂商结构化业务/原版AC命令及组证据、双AC HA/切换/恢复/实时归属、真实来源键/共享组、剩余工作台、原生Windows125%、跨机Web/TLS、目标机安装/SCM/升级卸载均待验收。通用断线自动重连/退避/命令重放尚未实现。Web继续loopback，离线候选需要目标机.NET10 ASP.NET Core Runtime；真实网络设备/Linux/堡垒机保持`REAL_DEVICE_PENDING`。无设备配置写入，未访问旧NetConsole数据根。

当前入口：`AUTONOMOUS_RUN_STATE.json`、`COMPLETE_TEST_MATRIX.md`、`IMPLEMENTATION_MATRIX.md`、`REMAINING_GAPS.md`、`FINAL_HANDOVER.md`。连接架构审计与原批次见 `../NETMIKO_CONNECTION_AUDIT_20261106.md` / `../WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md`；本轮证据见 `../AP_GROUP_SOURCE_IDENTITY_ACCEPTANCE_20261106.md`、`../AP_GROUP_CACHE_ACCEPTANCE_20261106.md`、`../AP_REPLACEMENT_F10_ACCEPTANCE_20261106.md`、`../CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md`、`../NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md`、`../DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md`。

## 以下为 2026-11-05 历史报告

下方原 HEAD、“当前”状态、凭据路径、失败项和测试数量只代表对应历史 checkpoint，不能覆盖上方最新结论；保留以便追溯。

2026-11-06 后续增量及当前边界见 `docs/NETMIKO_CONNECTION_AUDIT_20261106.md`；下方记录保留当时证据，不代表后续版本的最终验收。

日期：2026-11-05；Branch：`codex-A/netconsole-next-r3-dual-ac`；当前 HEAD：`5c600162a46932dc7064d0e2e34d9ed0dac01396`。未 push、未 merge、未 release。

## 结论

| 能力 | 状态 | 证据 |
|---|---|---|
| 多设备并行、并发上限、同设备互斥 | PASS（隔离 fixture） | `CONCURRENCY_P1_TESTS=5/5 PASS` |
| 单任务多命令 | PASS（隔离 fixture） | 命令计划、schema v16、H3C fact tests `13/13 PASS`；成功终态要求完整 Raw manifest 集合 |
| 独立 Raw、失败保留、Facts 发布 | PASS（隔离 fixture） | 多命令成功/后续失败测试 |
| 一键测试连接与快速添加 | PASS（API/UI/HTTP 回归） | onboarding endpoint、SSH/Telnet 默认端口、设备列表按钮、`web_auth_e2e.py` |
| SSH 自动信任/轮换、Telnet 回退 | PASS（服务/模拟 transport；AC1/AC2 live login） | 连接工作流构建、harness 与 AC1/AC2 Host Key AUTO_UPDATE |
| Netmiko worker 与 driver mapping | PASS（离线 worker contract） | CPython JSON-lines worker、已安装 driver allowlist、分页准备；`netmiko_worker_test.py` 2/2 PASS；真实设备互操作仍 pending |
| Profile-scoped credential resolution | PASS（隔离服务/连接证据） | 按 profile ID 读取凭据，API/Raw/日志不回显密码；AC1/AC2 自动认证 PASS |
| Site Jump Host probe/refresh | PASS（隔离服务/API） | Site 持久化、脱敏查询、worker probe、指纹变化 fail-closed、显式 refresh；Telnet 不套用 Jump Host |
| 历史任务按 ID 查询与恢复 | PASS（隔离回归） | 直接 ID 查询支持详情、重试和恢复，不依赖最近 100 条窗口 |
| Ping/TCP/DNS/LLDP 批量诊断 | PASS（隔离 fixture/API/UI） | `NETWORK_DIAGNOSTICS_TESTS=4/4 PASS`；LLDP captured scope 证据 |
| LLDP 关系、变化历史和 Site claims | PASS（离线 fixture/API/UI） | `H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS`；关系不伪造删除 |
| Ping/TCP/DNS/LLDP 批量诊断重启历史 | PASS（隔离 fixture） | `NETWORK_DIAGNOSTICS_TESTS=4/4 PASS`；服务对象重建后恢复终态批次 |
| 配置备份、版本历史、差异和下载 | PASS（隔离 fixture/API/UI） | `CONFIG_BACKUP_TESTS=1/1 PASS`；敏感字段脱敏、Raw 校验和 Site claims |
| 模块 registry、导航状态、设置页和错误页 | PASS（服务端/前端隔离验证） | schema v17 持久化、SUPERADMIN 启停 API、核心模块保护、重启保留；普通 ADMIN 403；设置页可读取并启停非核心模块；前端 shell 使用服务端状态覆盖 |
| 任务中心取消、重试、恢复 | PASS（隔离调度/API/UI） | 三条受 SUPERADMIN + Site scope 保护的 task action route；Vitest `5 files/10 tests`；1920/2560 fixture 实际点击重试 |
| Service-backed 浏览器 smoke | PASS（隔离真实 Service） | 真实登录、概览、设备、任务、工具、文件页面；1920×1080 |
| 四台模拟器真实认证、双 AC HA | PARTIAL | AC1/AC2 自动 SSH 登录与版本识别 PASS；SW 认证后 Exec 失败；FIT-AP 凭据拒绝；HA 未执行 |
| R3 无线身份持久化 | PARTIAL（隔离 Catalog） | schema v18、事务 upsert、稳定 AP ID、跨 AC Observation、唯一 MAC 部分证据受控关联、重启恢复；正式 replacement/candidate lifecycle 和实时模拟器证据仍缺 |
| 跨 AC collection manifest | PASS（隔离 Catalog） | schema v19 source revision manifest、逐 Observation provenance、Raw manifest ref 和重启恢复；未替代实时采集证据 |
| AP Group 只读详情、成员和 provenance | PASS（隔离 API/UI） | AC/domain 组列表、组详情、成员 route；当前 Site claims 过滤；R3 fixture 与浏览器点击验收 |
| AC Domain AP association 与 observation provenance | PASS（隔离 API） | API-W04 返回 source AC/domain/Site、identity、revision 和 Raw manifest reference；支持来源 AC 过滤 |
| 跨 AC collection manifest 查询与采集记录 UI | PASS（隔离 API/Catalog/UI） | manifest 列表/详情、按 source AC 过滤、当前 Site claims 重新聚合、Raw provenance 展示 |
| Site 权限 | PASS（既有合同） | scope guard 与服务端复核 |

密码仍只通过 DPAPI 引用读取；未进入浏览器、URL、SQLite、日志、Git 或报告。真实数据未修改。

## 2026-11-05 最终复核补充

- 此前 UI 提交 `1c54d38`：SUPERADMIN 设置页读取模块状态、启停非核心模块并禁用核心模块操作；Service-backed 浏览器 smoke 新增“系统设置/模块开放状态”检查，`1920×1080 PASS`。
- 串行 Service Tests Release build：0 warnings / 0 errors；既有完整 C# Service harness 证据保持有效；本次在并行 worker 改动存在时复跑，于 `ConcurrencyP1Tests.FailedTaskCanBeRetriedAsANewRun` 失败，需收敛并行改动后重跑。
- 前端既有 `vue-tsc`、Vitest（5 files / 10 tests）和 Vite build 证据保持有效；发布输出已更新并纳入版本控制。
- 最新隔离测试根已有 DPAPI 引用并完成逐台连接：AC1/AC2 自动 SSH 登录和版本识别 PASS；SW SSH 认证成功但 Exec 设备信息识别失败；FIT-AP Telnet 凭据被拒。双 AC HA、切换/恢复和实时 AP 归属仍未执行。
- 本轮补齐 AP Group 详情/成员/provenance 只读合同；不提供组写回，不把离线 fixture 当作实时模拟器证据。
- 收口回归证据：R3 API fixture、无线投影/runtime、任务并发、Raw→Facts、LLDP、Service/Tests build、Vitest、vue-tsc、Vite 和无线浏览器 fixture 均通过；HTTP service-backed gate 仍受既有单实例 Mutex 与模拟器凭据缺口限制。
- collection manifest 回归新增 source AC 详情过滤与未授权来源不泄露断言；该能力仍是隔离 fixture/Catalog 证据，不替代四台模拟器实时采集。
- 本轮新增连接 onboarding HTTP 回归：只接收 Credential Reference；默认 SSH/Telnet 端口为 22/23；响应不返回 credential reference 或 host key；未配置 DPAPI 凭据时返回 `CREDENTIAL_UNAVAILABLE`。
- 本轮新增任务中心固定只读多命令计划入口；支持选择 H3C 设备、顺序命令和单条超时，提交后复用既有 task-definition/run、Raw→Facts 和 Site scope 合同。

## Checkpoint L 增量

- 当前 HEAD：`5c600162a46932dc7064d0e2e34d9ed0dac01396`；Branch：`codex-A/netconsole-next-r3-dual-ac`；本 checkpoint 仅更新交接文档，未 push、merge 或 release。
- Netmiko CPython worker、显式 driver mapping、profile-scoped credential resolution、SSH HostKey 自动首次信任/轮换，以及 Site Jump Host 一次性探测与显式 fingerprint refresh 已纳入完成矩阵；Web Shell interactive worker channel 仍在进行集成验证。
- 模拟器证据保持分级：AC1 `192.168.56.2:22` PASS、AC2 `192.168.56.5:22` PASS；SW `192.168.56.3:22` SSH 认证成功但 Exec/设备识别 PARTIAL；FIT-AP `192.168.56.4:23` Telnet 凭据认证失败 BLOCKED；双 AC HA、切换、恢复和实时 AP 归属 PENDING。
- 当前定向验证：`python -m unittest tests/netmiko_worker_test.py` 2/2 PASS；Release build 0 warnings/0 errors。完整 Service harness 当前复跑在并发重试断言失败，保留为未解决验证项，不覆盖既有隔离 PASS 证据。

## Checkpoint K 增量

- 当前 HEAD：`440a84f`。
- A13 隔离 HTTP/API、完整 Service harness 与 Service-backed Browser smoke 复验通过。
- A27 跨 AC collection manifest 列表/详情、source AC 过滤、Site claims 聚合与无线页采集记录完成。
- 四台模拟器完整业务验收仍未完成；连接测试已能区分认证失败和认证后设备信息/Exec 失败，并保留脱敏结果。

## Checkpoint K2 增量

- 当前 HEAD：`eeb6427`；新增 TerminalCore TcpListener 回归，验证 Telnet `Login failed` 映射为 `AUTHENTICATION_FAILED`。
- 完整 Service harness 与 Release build 继续通过；A12 状态为 `PARTIAL_BLOCKED`，外部 Gate 仍为 FIT-AP 凭据、SW Exec 兼容性及双 AC HA/切换/恢复。
