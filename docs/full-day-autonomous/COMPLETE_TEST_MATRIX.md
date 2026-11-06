# 完整测试矩阵

## 2026-11-07 Traceroute 与定时备份检查点：253b814

Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `2927b35dfb6c9d5ae7e82f83548133f13b1668e6`；进度读取并发修复及当前功能检查点 `253b8144226091337227f29dca93c9378ebe99af`。全产品 **PARTIAL / REAL_DEVICE_PENDING**；保留当前分支，仅本地提交，不 Push、Merge、Release。

最终 Release Service/Tests 构建 0 warnings/errors，默认完整 Service harness session89977 EXIT0。定向诊断4/4、Traceroute14/14、定时计划15/15、原备份9/9 PASS；实际 HTTP→Service→CPython/Netmiko→合成回环SSH的计划15项和原备份28项 PASS（session44093），Traceroute HTTP使用实际回环ICMP。前端2927b35全量16 files/159 tests、typecheck/build PASS；253b814定向ToolsView12/12、typecheck及最终Vite2087模块构建PASS；计划Chrome11/11、Traceroute Chrome8/8、原备份Chrome9/9 PASS。Chrome为源码UI/API mock，DSF1.25仅是浏览器缩放证据；最终重试/取消交错由新增定向单测覆盖，浏览器截图早于253b814。

本批新增验证：定时批次保存终态后才释放元数据pin，完成元数据最多128条；Profile移除不提前归还执行槽；损坏可选计划文件只隔离计划子系统。诊断成功+取消混合保持PARTIAL，终态与持久化同步发布，PARTIAL历史可重启恢复。对应回归已纳入默认完整harness。

本批只用 Next Workspace 隔离fixture/回环，设备配置写入0，旧NetConsole真实数据根未访问或复制；用户Vite90348/127.0.0.1:18792保留。现有离线候选仍为ef57213，不含本批功能；本批未重新执行离线十门禁、远端CI、终端/Linux/Jump浏览器或真实设备验收，历史证据保留所属提交范围。

详细功能、限制、命令和证据路径见[本批专项验收](../TRACEROUTE_AND_BACKUP_SCHEDULE_ACCEPTANCE_20261107.md)。

## 以下为既有检查点（历史范围）

下文原有“当前/最终”、测试数量与候选仅对各自提交有效；本批以本节和专项验收为准。


## 2026-11-07 当前测试矩阵：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

证据提交范围：完整后端 session23456、Worker40/40、前端14files/140tests及typecheck通过于 `e93b382`；`ef57213` no-store修复后重跑Service Release构建、配置HTTP28与完整离线十项，未重复完整harness/Worker/前端单测。

| 门禁 | 实际结果 | 证据范围 |
| --- | --- | --- |
| Release Service/Tests | PASS，0 warnings/errors | e93b382构建Service/Tests；ef57213重建Service |
| 默认完整后端harness | e93b382 session23456 EXIT0 PASS | 配置9/9、helper30/30、evidence96/96、AC3/3及既有回归；header修复后未重跑 |
| Worker | e93b382 40/40 PASS | 精确交互/期限/大小/不重放及既有驱动恢复；header修复后未重跑 |
| 完整前端 | e93b382 14 files/140 tests、typecheck PASS | 单测/typecheck未因header修复重跑；当前候选Vite2084 modules PASS |
| AC HTTP/Chrome | 21 checks / 7 scenarios PASS | HTTP真实Netmiko合成SSH；Chrome源码UI/API mock，6张已检查截图 |
| 三类备份HTTP/Chrome | 28 checks / 9 scenarios PASS | 独立版本/同类diff/saved缺失/只读；Chrome3张已检查截图 |
| 全机诊断HTTP/Chrome | 24 checks / 9 scenarios PASS | direct/confirmed/未知问题/断线/取消/大小/脱敏/重启；Chrome4张已检查截图 |
| bundle / candidate build | 13581 / 64183 EXIT0 | 18 pinned/hash wheels、当前Vue、前后driver/Telnet smoke |
| 当前离线运行 | 71491 EXIT0，10标记PASS | 精确ef57213 ProductVersion、bundled依赖与隔离运行 |
| 资源核对 | 相关进程0、顶层offline-candidate目录0 | Vite90348/18792保留；最小SQLite fixture保留 |

当前候选十项均已执行通过：

```text
OFFLINE_BUNDLED_RUNTIME
OFFLINE_STATIC_UI
OFFLINE_NETMIKO_CONNECTION
OFFLINE_TASK_DIAGNOSTICS
OFFLINE_NETMIKO_RECONNECT
OFFLINE_LINUX_WEBSOCKET
OFFLINE_AC_RESOURCES
OFFLINE_CONFIGURATION_KINDS
OFFLINE_FULL_DIAGNOSTIC
OFFLINE_CANDIDATE
```

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-cache-fix-20261107`，ProductVersion `1.0.0+ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。新 bundle `build/netmiko-inheritance-final-20261106` 包含 18 个 pinned/hash wheels（CPython 3.13 / Netmiko 4.7 / Paramiko 4）；bundle build `13581`、candidate build `64183` 均 EXIT 0，Vue 2084 modules 与复制前后 driver/Telnet smoke PASS。离线运行 `71491` EXIT 0，十项标记全部 PASS。运行环境为 Production、隔离 cwd/数据、PATH 仅 System32，无 Python/worker 覆盖；未使用旧 NetConsole 生产数据。候选本机隔离运行不代表目标机安装，目标机仍需 .NET 10 ASP.NET Core Runtime。

配置离线覆盖三类独立版本、脱敏/no-store下载与saved缺失；诊断离线含direct/confirmed两分支、超过普通256KiB的回显、只回n及脱敏TXT/ZIP，共12 checks。

资源核对由主 Agent 完成：Next Workspace 相关 Service/Python/py 进程 0，`test-data` 顶层 `offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` Listen 保留。单个最小 SQLite fixture 按规则保留，不能写全部测试目录 0，确切路径见 [最终交接](FINAL_HANDOVER.md)。未访问/复制旧 NetConsole 数据根，真实设备配置写入 0。

此前VLAN/终端/Linux/Jump/有界恢复等专项未全部再次独立运行，下方保留原提交/会话的证据范围；远端CI、原生125%、真实设备、跨机TLS及目标机安装未完成。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。


## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

## 2026-11-06 当前检查点：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 整体 **PARTIAL / REAL_DEVICE_PENDING**。

| 门禁 | 当前结果 | 证据边界 |
| --- | --- | --- |
| Release Service / Tests build | 9fc499f PASS，0 warnings / 0 errors | 网络transport退役及分类 / Jump CAS修正后的最终构建 |
| 默认完整 Service harness | 最终session `31025`，EXIT 0 PASS | 包括重连172、ZTE12、scope13、VLAN6/publication14、诊断89及既有回归 |
| Netmiko有界恢复 / worker | C#172/172、Python29/29、HTTP13 checks PASS | 最多2次额外重连；发送前精确只读allowlist；不重放发送后未知结果 |
| ZTE / Task Site边界 | 12/12、13/13 PASS | 当前actor/Profile/Site/target复核；失败保留旧Current |
| 网络transport退役 / CI | 9fc499f COMPLETED_LOCAL；最终完整harness PASS | 旧网络SSH.NET移除；Linux保留；CI仅workflow与本机等价gates，远端未运行 |
| VLAN parser / publication | 6/6、14/14 PASS | 已测Comware detail、整表Current/history；不是其他厂商或真实型号兼容 |
| VLAN HTTP→Netmiko→合成 SSH | 25 checks PASS | 独立Service/隔离数据/真实CPython与回环SSH；最后分页与正文边界修复由后续完整harness覆盖 |
| VLAN Chrome源码UI / API mock | 6/6场景 PASS | 1920×1080 / 2560×1440；不执行后端或设备连接 |
| CLI诊断后端定向 | 89/89 PASS | 篡改/配置排除/partial/权限/64KiB撤权/取消/并发/下载零新增任务与文件 |
| CLI诊断 HTTP→Netmiko→合成 SSH | 21 checks PASS | TXT/ZIP/hash、权限/篡改、重启后无新设备命令；最终脱敏后大小边界由完整harness覆盖 |
| TasksView / Chrome诊断UI | Vue20/20、Chrome mock8/8 PASS | 浏览器实际保存TXT/ZIP，关闭/切任务/撤权/迟到隔离；浏览器使用模拟API |
| 完整前端 | Vitest14 files/124 tests、typecheck、Vite build PASS | 包含VLAN及诊断下载；既有大chunk提示保留 |
| 当前离线候选 | 9fc499f七项本机PASS，session16200 EXIT0 | 包括OFFLINE_NETMIKO_RECONNECT与TASK_DIAGNOSTICS；精确ProductVersion，Production/隔离数据/PATH仅System32 |

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`。未写设备配置、未访问旧数据根；不 Push、Merge、Release。有界发送前恢复、旧网络 transport 退役与精确 SHA 离线候选已通过本机门禁。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


终端最终回归：`terminal_ui_contract.py` 3 checks PASS；完整终端浏览器 E2E session `87828` EXIT 0，覆盖 Netmiko Telnet 自动认证、SSH / Telnet / AUTO、中文、COM、split/search/theme、ticket/history/replay/export 和 PTY resize；均为本机合成设备 / 浏览器范围。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 集成测试矩阵（历史）

以下保留该批测试数、session69905与五项候选门禁；其中“当前/最终”均指当时范围，不能作为34b524d候选证据。

2026-11-06 历史集成检查点：功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准。本轮 AUTO/legacy 策略、共享 worker 预算、专用批量连接、Terminal reason 修复和当前离线候选均已完成本机最终验证。整体 **PARTIAL**，实机保持 **REAL_DEVICE_PENDING**。

| 门禁 | 当前证据 | 证据边界 |
| --- | --- | --- |
| Release Service / Tests build | PASS，0 warnings / 0 errors | 最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` |
| 默认完整 Service harness | session `69905`，EXIT 0 PASS；Terminal reason 最终修复后 | 含策略6组、共享worker预算6组、批量连接8组及既有AP Group/cache/F10/runtime/Raw/并发/迁移/崩溃恢复；配置备份6组保留独立入口 |
| 配置备份定向 harness | CONFIG_BACKUP_TESTS=6/6 PASS | 完整行序/重复次数、超过16384行、大输入分配预算、目标预检/权限与既有备份回归 |
| Web typecheck / Vitest | PASS；12 files / 82 tests PASS | 包含配置备份与专用批量连接；批量专项17 tests，加既有设备详情定向23/23 PASS，不替代后端连接验证 |
| 配置备份 Chrome UI | 6 scenarios PASS | 1920×1080 DPR1；源码 UI + API mock，不表示现场采集 |
| 配置备份 HTTP→Netmiko | CONFIG_BACKUP_HTTP_NETMIKO_E2E=PASS (19 checks) | 实际 HTTP Service→CPython/Netmiko→合成回环 SSH，非真实设备兼容 |
| AP Group 来源身份/schema24 | 合同10场景、迁移/回滚、HTTP16、浏览器15 PASS | fda2a68；HTTP为隔离Service+离线fixture，浏览器为API mock |
| AP Group 候选缓存 | WIRELESS_GROUP_CACHE_TESTS=4/4 PASS | 2696390；仅可重建缓存收敛，不代表业务 ENDED |
| F10 conservative 更换隔离 | WIRELESS_REPLACEMENT_TESTS=3/3 PASS | 764d764；真实隔离SQLite、离线command output与fact: provenance；未生成实时Raw分段 |
| Wireless projection / runtime | Projection/Group合同 PASS；Runtime13/13 PASS | F10与缓存定向回归及完整harness；不代表正式mutation或HA |
| Netmiko AUTO/legacy策略 | H3C_CONNECTION_POLICY_TESTS=PASS (6 groups)；worker24/24 PASS | 显式允许AUTO仅协商/连接超时一次Telnet尝试；认证/HostKey/Jump/取消等不回退；H3C legacy限定条件一次兼容尝试，无通用命令重放 |
| Service实例共享worker预算 | NETMIKO_WORKER_BUDGET_TESTS=PASS (6 groups, shared limit 16) | 真实本机合成子进程；采集/单台测试/网络WebShell/Jump探测共享，等待可取消，进程实际退出后归还；不跨Service实例 |
| 专用批量连接harness | DEVICE_CONNECTION_TESTS=8/8 PASS；已纳入默认完整harness | 精确库存UUID、404优先、409零写入、权限/取消、关联重绑及映射完整性；最终完整Service回归PASS |
| 专用批量连接Chrome | 4 groups PASS | 真实Chrome源码Vue/Vite + 业务API mock；创建/刷新/取消、Site迟到隔离、ADMIN隐藏及1920/2560视口 |
| 专用批量连接HTTP→Netmiko | guard后最终复验14/14 PASS | 实际HTTP Service→CPython/Netmiko→合成loopback SSH；凭据/指纹、双目标报告、权限/模块、取消释放，不代表真实设备 |
| 本轮最终整合/提交 | PASS；功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` | Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码 |
| 当前离线候选 | `OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS | 路径 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`；ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留 |

本轮提交：`fda2a68` → `2696390` → `764d764` → `e12d062` → `8320e2f`；文档以所属提交为准。详见 `../AP_GROUP_SOURCE_IDENTITY_ACCEPTANCE_20261106.md`、`../AP_GROUP_CACHE_ACCEPTANCE_20261106.md`、`../AP_REPLACEMENT_F10_ACCEPTANCE_20261106.md`、`../CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md`、`../NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md`、`../DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md`。没有设备配置写入，未访问旧NetConsole数据根，不Push、Merge、Release。候选本机运行通过不替代目标机安装或实机兼容验收。

## a3954a5 连接批次历史验收

下表保留原批次结果。其“最终/当前”均只指该历史时点；候选未包含后续 Group/cache/F10/配置备份及最新协商补强，不作为当前候选 PASS。未改动能力的既有证据继续按原范围有效。


日期：2026-11-06。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）；整体 PARTIAL。下表中的本机或模拟证据不等于真实硬件兼容。

| 门禁 | 当时结果 | 证据边界 |
| --- | --- | --- |
| Service Release build | PASS，0 warning/error | 最后 scope 修复后 |
| 完整 Service harness | PASS | 含 ACL/Linux/凭据/Jump、原子 onboarding、任务/Raw/权限回归；旧并发重试失败已由当前通过结果替代 |
| Vue typecheck / Vite | PASS | 当前 Vue 源码 |
| Vitest | 8 files / 23 tests PASS | 页面/组件及既有回归 |
| Netmiko worker | 20 tests PASS | driver、交互、错误、超时/取消边界 |
| 离线工具回归 | 7 tests PASS | 打包 4 + 候选组合 3 |
| 网络终端完整 E2E | PASS | loopback SSH/Telnet/AUTO、中文、PTY resize、重连、分屏、COM、ticket、历史/回放/导出 |
| Linux 最终 HTTP/WebSocket + Edge（含完全静默撤权和 SSH 释放） | PASS | loopback SSH；CRUD、密码更新语义、中文/ANSI、控制键、resize、重连、多会话、Site/撤权、录制、重启/idle/delete；静默撤权 HTTP 回归 PASS |
| Site Jump 转发集成 | PASS | `ssh_jump_host_e2e.py`：真实回环 bastion + Netmiko；两 Site、指纹轮换、采集/WebSocket、并发释放/取消、错误、Telnet 绕过、重启/删除 |
| Site Jump Settings Chrome | PASS | `ssh_jump_host_browser_e2e.mjs`：API Mock；保存/留空/清除、probe、指纹、Site 切换、ADMIN 只读、主题/溢出 |
| ZTE 凭据 API/UI | PASS | 本机 HTTP 与组件；真实 ZTE 待验收 |
| 最终离线候选运行 | PASS | fresh Vite + 内置 CPython，独立 cwd/Production 隔离数据，PATH 仅 System32；内置 Netmiko 4.7.0/Paramiko 4.0.0、静态 hash、回环 H3C SSH/Linux WS 五项 PASS；ProductVersion 含完整 a3954a5；进程/本轮临时目录残留 0 |
| 当前 HCL Netmiko 模拟器 | AC1/AC2/SW PASS | 只读版本/AP/接口/LLDP 命令；FIT-AP AC 管理；配置写入 0 |
| 既有任务/Raw 专项 | PASS | 并发 5/5、Raw 完整 manifest 13/13、历史任务直接 ID 查询 |
| 诊断/备份/LLDP | PASS（隔离） | 诊断 4/4、配置备份 1/1、LLDP 2/2；不从连接命令测试推定全部实时业务验收 |
| R3 无线持久化/API/UI | PASS（隔离） | schema v18/v19、Observation、group/provenance、manifest scope/filter、重启恢复 |
| 模块治理/Service-backed browser | PASS（隔离） | SUPERADMIN 启停、核心保护、API 门禁、重启；已有页面导航与设置页 |
| 1920×1080 / 2560×1440 | PASS（已执行浏览器场景） | 视口不替代原生 125% |
| 双 AC HA/切换/恢复/实时 AP 归属 | PENDING | 不从登录成功推断 |
| Windows 原生 125% / 跨机 Web TLS | PENDING | 当前 loopback |
| 目标机安装/SCM/升级/卸载 | PENDING | framework-dependent，需要 .NET 10 ASP.NET Core Runtime |
| 实际网络设备/Linux/堡垒机 | REAL_DEVICE_PENDING | 回环与 HCL 不替代实机 |

HCL 证据：Workspace `reports/netmiko-simulator-20261106.json`。最终离线候选：Workspace `build/service-web-linux-jump-a3954a5-20261106`。无生产/旧真实开发数据写入；未 Push、Merge、Release。
