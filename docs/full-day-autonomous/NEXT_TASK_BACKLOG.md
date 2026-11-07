# 全天候动态任务池

## 2026-11-08 R7.1 当前入口

分支 `codex-A/netconsole-next-r3-dual-ac`；产品本地提交 `9135589`、`d40f971`、`5026584`、`2cb89b2`。七工作流多批次已集成；整体 **PARTIAL**。不Push/Merge/Release，设备配置写入0。

当前结论以 [最终验收](../r7-1-autonomous/R7_1_FINAL_ACCEPTANCE.md)、[剩余问题](../r7-1-autonomous/R7_1_REMAINING_GAPS.md)、[运行交接](../r7-1-autonomous/R7_1_RUNTIME_HANDOFF.md) 为准，下方历史TODO/PID/统计不代表当前结果。

Web27文件306项、typecheck/Vite2115modules PASS；完整Service harness `r71-service-harness-history-final.log` EXIT0，构建0warnings/errors；HTTP26组及最新静态浏览器登录/7业务步骤PASS，0错误。真实LLDP3邻居与旧5Current退役、精确筛选/CSV已验；自然AC1到期备份版本6/hash PASS且暂停。新SW备份跨重启明细可读，旧丢失明细明确禁用，128条/4MiB终态元数据持久化。

| 能力 | 当前结论 / 下一门禁 |
| --- | --- |
| 完整配置批量/固定范围/计划/差异导出 | 已实现并经真实只读备份验证；旧明细不追造，磁盘长期保留另验 |
| 故障生命周期/聚合/补采/重试/历史/导出 | 已实现；真实业务恢复及长期无人值守待验，默认不启用补采 |
| Syslog查询/诊断/CSV/分类、拓扑/运维入口 | 已实现；远端设备Syslog、长期保留与更多原版能力继续迁移 |
| 任意历史整机恢复 | BLOCKED：native checkpoint/transfer/staging/replace未验证，不将描述Writer作为整机恢复 |
| 双AC切换/业务恢复 | NOT_TESTED：Master/Backup/Ready观察已得，无已验证可恢复切换路径，不断共享管理链路 |
| FIT直接认证 | AUTH_CREDENTIAL_UNAVAILABLE：不猜密；经AC身份观察不替代直接CLI |
| Windows SCM | PENDING_EXTERNAL：实际安装/自启/崩溃恢复/升级回滚另验 |
| 精确候选apphost ICMP | FAIL/PARTIAL：权限根因未定位，重跑原Gate前不得升级PASS |
| 无线权威/Mesh/长期fping/多厂商/现场兼容 | PARTIAL，按剩余问题继续实施与验收 |

保留Vite `http://127.0.0.1:5174` PID37420；Service/静态UI `http://127.0.0.1:18771` PID85584。HEALTHY/doctor PASS，两验收计划暂停；最终DLLhash及日志见交接。不要另起同根Service或停止其它任务进程；未访问旧真实数据根。

## R7.0 及更早检查点（完整历史）

下方保留原始检查点与证据。其 TODO/未实现及优先级不再是当前任务池：完整私有备份批量/计划和 Syslog viewer 已在 R7.1 实现；恢复、HA、FIT、SCM 与候选 ICMP 等仍以上方及 R7.1 专项为准。

## 2026-11-08 R7.0 增量与优先级（历史）

当前分支 `codex-A/netconsole-next-r3-dual-ac`，R7起点 `2551b26`；最终功能提交与Gate见 `docs/r7-autonomous/R7_FINAL_ACCEPTANCE.md`，不使用下方历史检查点作为本轮HEAD或验收。整体 **PARTIAL**；不Push、Merge、Release。

| ID | 优先级 | 本轮实际增量 / 剩余任务 | 状态与验收边界 |
| --- | --- | --- | --- |
| R70-E-01 | P1 | Quick Ping连续采样/统计/取消/历史 | IMPLEMENTED_PARTIAL；.NET ICMP/TCP、2–60样本/8目标/单目标120s；真实TCP链通过；原版长期fping/压缩保留/IP占用未迁移 |
| R70-B-01 | P1 | 健康/故障证据、显式只读恢复与补采结果 | IMPLEMENTED_PARTIAL；健康7/7、Vue5/5，真实AC只读连接/补采链；不代表宕机或业务恢复 |
| R70-D-01 | P1 | 设备详情真实能力、无线角色/能力证据 | IMPLEMENTED_PARTIAL；设备详情13/13/typecheck；UNKNOWN/STALE/来源清晰；新部署浏览器复验由最终Gate记录 |
| R70-A-01 | P0 | 完整配置上下文比较、脱敏导出、实时恢复预览 | IMPLEMENTED_PARTIAL；比较10/10；预览writeAllowed=false，任意历史整机恢复未实现 |
| R70-A-02 | P0 | 原生检查点/replace实际互操作 | BLOCKED；真实SW commit无新增ID，NATIVE_CHECKPOINT_NOT_CREATED；replace交互未证实，描述/replace前阻断；测试14/14不等于实机PASS |
| R70-C-01 | P1外部 | 双AC实际切换与关键业务恢复 | BLOCKED_EXTERNAL；两AC Common、同AP R/M/R/B，不是DEVICE_VERIFIED_HA；不执行未知切换 |
| R70-D-02 | P1外部 | FIT原生Telnet认证 | BLOCKED_EXTERNAL；已有凭据曾拒绝，不猜密不重复撞密；AC托管身份/组/观测继续可用 |
| R70-G-01 | P0 | doctor、新部署最终Gate、Windows安装/SCM | PARTIAL；doctor10/10及源码实际运行通过，SCM语法通过；安装/崩溃/升级/独立包路径未完整验收 |
| R70-A-03 | P1 | 完整私有备份批量/Site/Domain/定时 | TODO；现有私有单设备和旧脱敏批量/计划必须分开，不能宣称同一能力 |
| R70-E-02 | P1 | Syslog授权查询/筛选/原文Vue viewer | TODO；后端UDP/Raw基础存在但无独立viewer，当前端口0，不称实时日志可用 |
| R70-E-03 | P1 | 原版长期Quick Ping迁移合同 | TODO；先定义资源上限/终止/进程身份/保留，不把有界采样改成无界循环 |
| R70-E-04 | P2 | Mesh/MR专业分析报表、长期健康通知、图形拓扑 | TODO；已有通用事实/导出不等于专项产品闭环 |
| R70-E-05 | P1 | 25项迁移矩阵/状态/报告 | UPDATED；逐项原版源码/API/UI/测试/操作边界，整体PARTIAL |

优先按真实阻断和业务价值推进：最终运行验收→受支持设备上的安全恢复→完整私有备份与Syslog查看→长期Ping合同→专业分析/报表。外部认证/HA受阻不阻断其它产品任务。详细映射见 [25项矩阵](../r6-feature-parity/ORIGINAL_TO_NEXT_FEATURE_MATRIX.md) 和 [R7迁移报告](../r7-autonomous/R7_ORIGINAL_FEATURE_MIGRATION_REPORT.md)。

下文为历史检查点；其中“当前”“下一优先级”“未实现”仅对其所属提交有效，R7状态以上方与最终验收为准。


## 2026-11-07 Continuation checkpoint：41bd2e2

Branch：`codex-A/netconsole-next-r3-dual-ac`。本轮已完成并本地提交：同设备取消竞态、STARTUP_BODY 两阶段只读采集/UI 与 restore 明确拒绝边界、R3 identity review/revision 只读合同。整体仍 **PARTIAL / REAL_DEVICE_PENDING**，不 Push、Merge、Release。

| ID | 任务 | 状态 | 验收与边界 |
| --- | --- | --- | --- |
| FD-P1-01 | 同设备互斥等待取消 | COMPLETED_LOCAL | `CONCURRENCY_P1_TESTS=6/6 PASS`；不创建第二会话 |
| FD-P2-06B | STARTUP_BODY 正文采集 | COMPLETED_LOCAL | `CONFIG_BACKUP_TESTS=11/11 PASS`；严格 `.cfg` 路径、两阶段顺序、UI/API、Raw/claims/history/download/diff 复用 |
| FD-P2-09B | STARTUP_BODY 计划边界 | COMPLETED_LOCAL | `CONFIG_BACKUP_SCHEDULE_TESTS=15/15 PASS`；定时计划拒绝正文采集 |
| FD-P1-30A | AP identity review/revision | COMPLETED_LOCAL_READONLY | `WIRELESS_IDENTITY_CONTRACT_TESTS=3/3 PASS`；无权威 mutation |
| FD-P1-30B | identity merge/split/replacement 写合同 | CONTRACT_PENDING | 需要 revision 并发、撤销和来源权威定义 |
| FD-P1-05 / FD-P1-18 | 双 AC HA、切换/恢复、实时归属 | PARTIAL_EXTERNAL | 真实认证和运行证据待补 |
| FD-P1-26 | 当前 HEAD 离线候选 | PENDING_CURRENT_BATCH | `86d3190` 尚未重建当前候选 |

下一优先级：定义 identity 写合同；获取稳定 GroupEvidence；在凭据可用时执行双 AC HA/实时 AP 归属；重新构建当前 HEAD 隔离候选。

## 2026-11-07 Traceroute 与定时备份检查点：253b814

Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `2927b35dfb6c9d5ae7e82f83548133f13b1668e6`；进度读取并发修复及当前功能检查点 `253b8144226091337227f29dca93c9378ebe99af`。全产品 **PARTIAL / REAL_DEVICE_PENDING**；保留当前分支，仅本地提交，不 Push、Merge、Release。

| ID | 任务 | 状态 | 验收与边界 |
| --- | --- | --- | --- |
| FD-P2-08 | ICMP Traceroute与结构化路径历史 | COMPLETED_LOCAL | 后端14/14、诊断4/4、HTTP实际回环ICMP、Chrome8/8；最多30跳/共享总期限/当前目标复核/部分路径 |
| FD-P2-09 | 持久化定时配置备份 | COMPLETED_LOCAL | 后端15/15、HTTP15、Chrome11/11；复用Netmiko和既有任务，60–43200分钟、不重叠/无追赶循环，元数据有界 |
| FD-P1-26 | 当前功能SHA离线候选 | PENDING_CURRENT_BATCH | ef57213十门禁仍是历史通过，不含2927b35；当前Windows源码Service/HTTP回归通过 |
| FD-P1-30 / FD-P1-05 / FD-P1-18 | 生命周期权威写合同、完整无线与HA | CONTRACT_PENDING / PARTIAL | 不从缺席推断ENDED，现场证据待补 |
| FD-P3-04 | 原生125%、跨机TLS与目标机部署 | PENDING_EXTERNAL | 本机浏览器视口/DSF和回环不替代 |

最终 Release Service/Tests 构建 0 warnings/errors，默认完整 Service harness session89977 EXIT0。定向诊断4/4、Traceroute14/14、定时计划15/15、原备份9/9 PASS；实际 HTTP→Service→CPython/Netmiko→合成回环SSH的计划15项和原备份28项 PASS（session44093），Traceroute HTTP使用实际回环ICMP。前端2927b35全量16 files/159 tests、typecheck/build PASS；253b814定向ToolsView12/12、typecheck及最终Vite2087模块构建PASS；计划Chrome11/11、Traceroute Chrome8/8、原备份Chrome9/9 PASS。Chrome为源码UI/API mock，DSF1.25仅是浏览器缩放证据；最终重试/取消交错由新增定向单测覆盖，浏览器截图早于253b814。

下一步：当前功能SHA离线候选独立验收、稳定厂商GroupEvidence及生命周期权威写合同；继续startup正文/restore、其他厂商结构化业务、双AC HA/实时归属及剩余工作台。Windows原生125%、跨机TLS、目标机安装/SCM/升级卸载与实际设备兼容仍待，不从缺席推断ENDED。

详细功能、限制、命令和证据路径见[本批专项验收](../TRACEROUTE_AND_BACKUP_SCHEDULE_ACCEPTANCE_20261107.md)。

## 以下为既有检查点（历史范围）

下文原有“当前/最终”、测试数量与候选仅对各自提交有效；本批以本节和专项验收为准。


## 2026-11-07 当前任务池：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

| ID | 任务 | 状态 | 当前验收与边界 |
| --- | --- | --- | --- |
| FD-P1-37 | H3C AC 12 条资源命令与独立回显 | COMPLETED_LOCAL | 后端3/3、HTTP21、Browser7、离线AC_RESOURCES PASS；组名仅形成来源AC范围CANDIDATE |
| FD-P2-06 | RUNNING/SAVED/STARTUP_INFO 三类只读备份 | COMPLETED_LOCAL | 后端9/9、HTTP28、Browser9、离线CONFIGURATION_KINDS PASS；独立版本/同类diff、脱敏/no-store下载；startup正文/restore仍待 |
| FD-P2-07 | display diagnostic-information 全机诊断 | COMPLETED_LOCAL | helper30/30、evidence96/96、HTTP24、Browser9、离线FULL_DIAGNOSTIC PASS；540s/4MiB/仅精确问题回n，不重放 |
| FD-P1-26 | 精确当前产品SHA离线候选 | COMPLETED_LOCAL | ef57213 ProductVersion，runtime71491 EXIT0，十标记PASS；非安装或实机验收 |
| FD-P1-30 | candidate/identity/replacement与可靠终止合同 | CONTRACT_PENDING | 确认/拒绝、合并/拆分、写入/撤销/revision、Domain/group生命周期待定义 |
| FD-P1-05 / FD-P1-18 | 无线完整业务、HA、切换/恢复与实时归属 | PARTIAL / PARTIAL_EXTERNAL | 既有隔离/HCL证据保留；生产GroupEvidence与现场业务证据待补 |
| FD-P3-04 | 原生125%、跨机TLS与目标机部署 | PENDING_EXTERNAL | 本机视口、回环与framework-dependent候选不替代 |
| FD-P3-03 | 当前矩阵、状态与交接 | UPDATED_LOCAL | 已同步功能提交、测试版本范围、离线候选与剩余缺口 |

推进顺序：先取得稳定厂商GroupEvidence和固件回显、定义identity/candidate/replacement/Domain权威写合同，再扩展结构化业务与HA；startup正文/restore独立设计验收。

功能提交 `e93b382` 的完整后端 harness session `23456` EXIT 0 PASS，包含配置备份 9/9、全机诊断 helper 30/30、诊断证据 96/96、AC 资源 3/3 及既有回归；同提交 Worker 40/40、前端 14 files / 140 tests 和 typecheck PASS。`ef57213` 的 no-store 修复后重跑 Service Release 构建、配置 HTTP 28 项与完整离线十项门禁，未重复完整 harness、Worker 或前端单测。浏览器为真实 Chrome/源码 UI/API mock，HTTP 为实际 Service → CPython/Netmiko → 合成回环 SSH，均不替代真实型号验收。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-cache-fix-20261107`，ProductVersion `1.0.0+ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。新 bundle `build/netmiko-inheritance-final-20261106` 包含 18 个 pinned/hash wheels（CPython 3.13 / Netmiko 4.7 / Paramiko 4）；bundle build `13581`、candidate build `64183` 均 EXIT 0，Vue 2084 modules 与复制前后 driver/Telnet smoke PASS。离线运行 `71491` EXIT 0，十项标记全部 PASS。运行环境为 Production、隔离 cwd/数据、PATH 仅 System32，无 Python/worker 覆盖；未使用旧 NetConsole 生产数据。候选本机隔离运行不代表目标机安装，目标机仍需 .NET 10 ASP.NET Core Runtime。

资源核对由主 Agent 完成：Next Workspace 相关 Service/Python/py 进程 0，`test-data` 顶层 `offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` Listen 保留。单个最小 SQLite fixture 按规则保留，不能写全部测试目录 0，确切路径见 [最终交接](FINAL_HANDOVER.md)。未访问/复制旧 NetConsole 数据根，真实设备配置写入 0。

剩余：稳定生产 `GroupEvidence`、startup 文件正文/restore、其他固件与厂商的结构化业务、candidate 确认/拒绝、identity 合并/拆分、replacement 写入/撤销/revision 合同、可靠终止与 Domain/group 生命周期、双 AC HA/实时归属、剩余工作台、Windows 原生 125%、跨机 Web/TLS 与目标机安装/SCM/升级/卸载；不从缺席推断 `ENDED`。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。

## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

## 2026-11-06 当前检查点：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 整体 **PARTIAL / REAL_DEVICE_PENDING**。

VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

有界发送前恢复、旧网络 transport 退役与精确 SHA 离线候选已通过本机门禁；其他剩余范围见 `REMAINING_GAPS.md`。不 Push、Merge、Release，不写设备配置或访问旧数据根。本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

## 当前任务

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
| --- | --- | --- | --- | --- | --- | --- |
| FD-P1-27 | Wireless | AP Group 来源键、身份与成员版本 | P1 | explicit evidence/Site claims/schema24 | COMPLETED_LOCAL | fda2a68；合同10场景、迁移、HTTP16、浏览器15 PASS；实时来源键/共享证据待验收 |
| FD-P1-28 | Wireless | AP Group 派生候选缓存收敛 | P1 | FULL 授权投影/逐来源时间水位 | COMPLETED_LOCAL | 2696390；缓存4/4 PASS；不删除 Raw/Facts/历史 Observation，不推断组 ENDED |
| FD-P1-29 | Wireless | F10 conservative replacement 隔离 | P1 | 既有 identity/runtime/provenance 合同 | COMPLETED_LOCAL | 764d764；3/3 PASS；独立身份/历史/来源隔离，不代表正式 replacement 写流程 |
| FD-P1-30 | Wireless | 正式 candidate/identity/replacement 写流程与关系终止 | P1 | 权威写入/撤销/revision 并发合同 | CONTRACT_PENDING | 确认/拒绝、合并/拆分、replacement link 写入及可靠终止证据仍待定义，不从缺席推断 ENDED |
| FD-P1-31 | Connection | Netmiko AUTO/legacy 轻量协商补强 | P1 | 共用连接入口/错误分类/取消 | COMPLETED_LOCAL | 策略6组、worker24/24、Terminal reason修复后的最终默认完整Service harness session69905 EXIT0及Jump两项PASS；功能提交8320e2f，不引入第二套SSH/Telnet实现 |
| FD-P1-32 | Connection/Device UI | 专用批量连接API与设备页入口 | P1 | 精确库存UUID/Profile/现有任务计划 | COMPLETED_LOCAL | 定向8/8已纳入最终完整harness、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko14 checks与最终Jump两项PASS；限独立H3C只读版本命令 |
| FD-P1-33 | Connection/Resources | Service实例共享Netmiko worker上限16 | P1 | 进程lease/启动退出/可取消等待 | COMPLETED_LOCAL | 真实合成子进程预算6组、最终完整Service harness session69905 EXIT0及Jump两项PASS；采集/单台测试/网络WebShell/Jump探测共享，实际退出后归还 |
| FD-P1-34 | Collection/Facts/UI | H3C VLAN detail采集与整表快照 | P1 | 统一Netmiko/Raw manifest/Facts | COMPLETED_LOCAL | 170bf81；parser6/6、publication14/14、HTTP合成SSH25 checks、Chrome mock6/6 PASS；仅已测Comware detail，真实型号待验收 |
| FD-P1-35 | Connection/Security | Netmiko有界发送前恢复与实时撤权边界 | P1 | 统一worker/原任务期限/精确只读allowlist | COMPLETED_LOCAL | 4ee1cb7；C#172/172、worker29/29、HTTP13 checks、ZTE12/12、scope13/13 PASS；发送后未知结果不重放 |
| FD-P1-36 | Connection/CI | 退役重复网络transport及CPython 3.13真实Netmiko门禁 | P1 | FD-P1-35/分类修复/统一连接入口 | COMPLETED_LOCAL | 9fc499f；Release 0 warning/error、最终完整harness31025 EXIT0、terminal contract3 checks、完整终端浏览器87828 EXIT0；Linux SSH.NET保留，CI仅workflow及本机等价门禁 |
| FD-P2-05 | Task Center/Diagnostics | 任务封存诊断TXT/ZIP下载 | P2 | Raw完整性/当前会话与Site scope | COMPLETED_LOCAL | 34b524d；后端89/89、HTTP合成SSH21 checks、TasksView20/20、Chrome mock8/8、离线诊断门禁PASS；不重新连接或采集，配置输出排除 |

下表为既有连接能力与本轮状态。


连接批次历史基线：2026-11-06，`a3954a5`（起点 `4dac9fc`）；下表保留既有能力，并按本轮证据更新备份、文档和候选状态。worker20与离线工具7 tests是历史批次证据；本轮worker29/29、有界恢复、预算与批量入口结果见上表，不以旧候选替代新候选。**整体 PARTIAL**；COMPLETED_LOCAL仅表示列出的本机范围。真实设备/主机/堡垒机仍 `REAL_DEVICE_PENDING`。不Push、Merge、Release。

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
| --- | --- | --- | --- | --- | --- | --- |
| FD-P0-01 | Security | 凭据、密码、HostKey、Site 权限无泄漏 | P0 | Profile 密码/数据根 ACL/claims | COMPLETED_LOCAL | 最终 Service harness、密码脱敏与撤权回归 PASS |
| FD-P1-01 | Scheduler | 批量子任务、并发上限、同设备互斥 | P1 | BoundedCollectionScheduler | COMPLETED_LOCAL | CONCURRENCY_P1_TESTS=5/5 PASS |
| FD-P1-02 | Scheduler | 多命令顺序、命令级状态、独立 Raw | P1 | Raw manifest 合同 | COMPLETED_LOCAL | 原批次13/13；当前publication14/14；缺完整manifest拒绝成功终态 |
| FD-P1-03 | Connection | SSH 首次自动信任、指纹轮换和并发更新 | P1 | 认证成功/HostKey Catalog | COMPLETED_LOCAL | 网络/Linux/Jump 回环验证；旧 STRICT 采用自动策略 |
| FD-P1-04 | Connection | 一键连接测试和中文错误分类 | P1 | Profile/Netmiko worker | COMPLETED_LOCAL | API/设备列表与 HCL AC1/AC2/SW PASS |
| FD-P1-05 | Wireless | 双 AC/AP identity、Observation、group 和 runtime | P1 | R3 projection | PARTIAL | fixture PASS；正式生命周期仍需实现，HA/live ownership 待验收 |
| FD-P1-06 | Facts | 多命令 manifest 与 Raw→Facts→Current | P1 | FD-P1-02 | COMPLETED_LOCAL | 当前14/14 publication tests；隔离collection provenance，原批次13/13保留历史 |
| FD-P1-07 | Task UI | 业务进度、命令明细、重试/取消/恢复 | P1 | FD-P1-01/02 | COMPLETED_LOCAL | API/UI 与当前 Service harness PASS |
| FD-P1-08 | Device UI | 默认端口、Site、分组和连接测试 | P1 | FD-P1-04 | COMPLETED_LOCAL | 快速添加与原子库存/Profile 保存 |
| FD-P2-01 | Diagnostics | Ping/TCP/DNS/LLDP 批量诊断与历史 | P2 | scheduler/catalog | COMPLETED_LOCAL | NETWORK_DIAGNOSTICS_TESTS=4/4 PASS；不是全部实时业务证据 |
| FD-P2-02 | Backup | 配置备份、版本、差异与授权下载 | P2 | Raw/archive/claims | COMPLETED_LOCAL | e12d062：6/6、Web 65、Chrome 6场景、HTTP→Netmiko 19 checks PASS（隔离） |
| FD-P2-03 | Topology | LLDP 关系与变化历史 | P2 | verified facts | COMPLETED_LOCAL | H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS（隔离） |
| FD-P3-01 | UI | 1920/2560 页面交互 | P3 | UI workstream | COMPLETED_LOCAL | 浏览器视口 PASS；Windows 原生 125% 另项待验收 |
| FD-P3-02 | Integration | module registry/navigation/error states | P3 | frontend build | COMPLETED_LOCAL | typecheck/browser PASS；剩余完整工作台未完成 |
| FD-P2-04 | Integration/Security | 模块持久化、SUPERADMIN 启停和核心保护 | P2 | schema v17/WebAuth | COMPLETED_LOCAL | API/重启/Settings 浏览器 PASS |
| FD-P1-09 | Wireless/Facts | AP identity、AC Domain、跨 AC Observation、AP Group 持久化 | P1 | schema v18 | COMPLETED_LOCAL | 隔离持久化/重启 PASS；不代表正式生命周期 |
| FD-P1-10 | Wireless/Facts | 跨 AC manifest 与 provenance 绑定 | P1 | FD-P1-09/Raw | COMPLETED_LOCAL | schema v19 manifest/restart PASS |
| FD-P3-03 | Docs | 当前矩阵、回执、历史边界和handoff | P3 | 最终集成证据/本地提交 | COMPLETED_LOCAL | 已同步9fc499f、完整harness31025、重连172、终端87828、Jump65279、候选16200七项PASS、清理0；历史区保留，整体PARTIAL；文档以所属提交为准 |
| FD-P1-11 | Wireless/Facts | AP Group 详情、成员和 provenance 只读 API/UI | P1 | FD-P1-09/Site claims | COMPLETED_LOCAL | scope/detail API 与 fixture 浏览器 PASS |
| FD-P1-12 | Wireless/Facts | AC Domain AP association/provenance 只读 API | P1 | FD-P1-09/Site claims | COMPLETED_LOCAL | API-W04 fixture/filter PASS |
| FD-P1-13 | Wireless/Facts | 跨 AC manifest 查询/详情和记录 UI | P1 | FD-P1-10/Site claims | COMPLETED_LOCAL | API/Catalog scope 与 fixture 点击 PASS |
| FD-P1-14 | Connection/Device UI | 用户名/密码快速添加与原子设备/Profile 保存 | P1 | Profile/Catalog 事务/ACL | COMPLETED_LOCAL | onboarding HTTP、回滚冲突、Vue/API 回归 PASS |
| FD-P1-15 | Task Center | 超过最近 100 条仍可按 ID 查询/重试/恢复 | P1 | task_runs | COMPLETED_LOCAL | CONCURRENCY_P1_TESTS=5/5 PASS |
| FD-P1-16 | Task Center/UI | 固定只读多命令计划和命令报告 | P1 | H3C task-definition/Site scope | COMPLETED_LOCAL | web_auth_e2e、Vue、Service harness PASS |
| FD-P1-17 | Connection | Netmiko 错误分类、超时、取消与连接释放 | P1 | 统一 worker/service | COMPLETED_LOCAL | worker 20 tests；当前 SW 版本/接口/LLDP PASS |
| FD-P1-18 | Simulator | AC/SW 自动登录与双 AC HA/切换/恢复 | P1 | Netmiko；FIT-AP 经 AC 管理 | PARTIAL_EXTERNAL | AC1/AC2/SW 只读 PASS；HA/failover/live ownership 待验收 |
| FD-P1-19 | Task Center | 多目标父计划、子快照、聚合与取消 | P1 | scheduler/TaskSiteScope | COMPLETED_LOCAL | TASK_PLAN_PERSISTENCE_TESTS=PASS |
| FD-P1-20 | Connection/Netmiko | 统一 CPython worker 与 SSH/Telnet 驱动映射 | P1 | Netmiko 4.7.0/离线 CPython | COMPLETED_LOCAL | worker 20 tests；HCL PASS；真实型号 REAL_DEVICE_PENDING |
| FD-P1-21 | Security/Connection | Profile 密码自动读取与响应脱敏 | P1 | H3C/ZTE profile/ACL | COMPLETED_LOCAL | SQLite 明文字段受 ACL 保护；不使用普通设备 DPAPI 中心 |
| FD-P1-22 | Connection/Security | Site Jump 配置/转发与认证后指纹自动轮换 | P1 | Netmiko/Paramiko direct-tcpip/Site claims | COMPLETED_LOCAL | 真实回环两 Site、并发/取消/重启/错误 PASS；Settings Chrome PASS |
| FD-P1-23 | Connection/Terminal | Netmiko 交互通道与网络 Web Shell | P1 | worker/TerminalSessionManager | COMPLETED_LOCAL | 完整网络终端 E2E PASS；resize/reconnect/录制 |
| FD-P1-24 | Linux/Terminal | Linux inventory 与 SSH.NET ShellStream | P1 | Site/WS/独立 Linux 传输 | COMPLETED_LOCAL | 最终 Edge 浏览器 + HTTP/WS + restart/idle/delete PASS；含完全静默撤权与 SSH 释放 |
| FD-P1-25 | ZTE/UI | ZTE 新增/密码保留清除/采集入口 | P1 | ZTE profile/Netmiko | COMPLETED_LOCAL | HTTP/组件 PASS；真实 ZTE 待验收 |
| FD-P1-26 | Offline | 当前Vue+CPython离线候选构建与运行 | P1 | 9fc499f/Windows x64/.NET10 Runtime | COMPLETED_LOCAL | build/service-9fc499f-20261106，ProductVersion精确9fc499f完整SHA；构建2553及离线16200 EXIT0，七项PASS；最终进程/临时目录残留0，Vite90348保留，安装/SCM未验收 |
| FD-P3-04 | UI/Deployment | 原生 125%、跨机 Web/TLS、目标机安装 | P3 | 真实 Windows/跨机环境 | PENDING_EXTERNAL | 不得用浏览器缩放或本机候选运行替代 |

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 任务池检查点（历史）

以下保留该批原文；其中“当前/最终”和五项候选门禁均限当时范围。

2026-11-06 当前集成检查点：最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准。AP Group 来源身份 `fda2a68`、候选缓存收敛 `2696390`、F10 conservative 更换隔离 `764d764`、设备配置备份入口与有界差异 `e12d062` 及Netmiko策略/预算/批量连接与Terminal reason修复 `8320e2f` 已完成各自本机验收。整体 **PARTIAL**，不 Push、Merge、Release。

Netmiko轻量AUTO/legacy策略6组、worker24/24、Service实例共享worker上限16的预算6组、专用批量连接定向8/8、完整Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko→合成回环SSH14 checks均PASS。最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选只作历史，当前候选不是正式发布。
# 2026-11-07 R4 continuation backlog：2a4d65f

| ID | 任务 | 状态 | 下一步 |
| --- | --- | --- | --- |
| R4-IDENTITY | schema-backed identity authority、merge/split/revoke | PARTIAL_LOCAL | 保留 evidence、Site、revision、audit gates，补正式存储并发 |
| R4-RESTORE | 白名单设备 writer、失败回滚和现场证据 | SAFE_PREVIEW_ONLY | 凭据和 baseline/recovery evidence 到位后再执行 |
| R4-HA | 双 AC 1+1、切换恢复、AP ownership | BLOCKED_CREDENTIALS | 只访问四个授权端点，不猜凭据 |
| R4-CANDIDATE | 当前 HEAD 离线候选 | PENDING | 独立构建、运行和清理验证 |
