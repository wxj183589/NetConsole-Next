# 自动任务池

## 2026-11-06 当前任务状态：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 整体 **PARTIAL / REAL_DEVICE_PENDING**。

FD-P1-34 H3C VLAN 采集/整表 Current 与历史、FD-P2-05 CLI 诊断 TXT/ZIP 下载均为 `COMPLETED_LOCAL`。VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

仍待正式 candidate/identity/replacement 写流程、关系终止与 Domain/group 生命周期，双 AC HA/实时归属、完整 AC 命令与稳定 GroupEvidence、保存/启动配置、其他厂商结构化业务、剩余工作台、Windows 原生 125%、跨机 Web/TLS、目标机安装与实际设备兼容。 有界发送前恢复、旧网络 transport 退役与精确 SHA 离线候选已通过本机门禁。本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

保留当前分支，不 Push、Merge、Release；未写设备配置、未访问旧数据根。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 自动任务池（历史）

以下保留当时任务、测试和候选；其中 VLAN 未实现及旧候选描述不代表当前状态。

更新：2026-11-06。总体 **PARTIAL**，详细任务 ID 与状态见 `NEXT_TASK_BACKLOG.md`。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。

已完成本机验证：任务并发/恢复、多命令Raw/Facts、诊断/LLDP、模块治理、R3 provenance/manifest、设备原子保存与密码自动读取、Netmiko网络连接和交互终端、ZTE凭据UI、Linux服务器终端与Site Jump Host。已提交增量为AP Group来源身份 `fda2a68`、候选缓存收敛 `2696390`、F10 conservative更换隔离 `764d764` 和配置备份入口/有界差异 `e12d062`。备份6/6、Chrome mock备份6场景、实际HTTP→Netmiko备份19 checks PASS。

本轮新增AUTO/legacy策略6组、worker24/24、Service实例共享worker上限16的预算6组、专用批量连接定向8/8、完整Web12 files/82 tests/typecheck、Chrome mock4组、实际HTTP→Netmiko→合成回环SSH14 checks均PASS。专用批量入口缺失已关闭，限显式库存UUID、独立H3C、只读版本命令，复用既有任务计划与统一Netmiko；FIT-AP仍经AC管理。Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，配置备份6组为独立入口；最终Release构建0 warnings / 0 errors。

已完成本机最终整合与候选验收：功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`。fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选仅作历史；当前候选不是正式发布。

当前模拟器：AC1/AC2/SW 通过 Netmiko 自动登录和只读命令；FIT-AP 由 AC 管理。不存在独立 FIT-AP 凭据或旧 SW Exec 前置阻断。

下一批：定义candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销与revision并发合同，以及可靠终止证据和Domain/group生命周期。F10已验证新旧身份、运行态、离线命令输出与来源隔离；它未实现上述写操作，也未从缺席推断ENDED。VLAN实时CLI采集、完整多厂商结构化业务、双AC HA/切换/恢复与实时归属、剩余工作台、原生Windows125%、跨机Web/TLS、目标机安装/SCM/升级/卸载和实机兼容继续待完成。

保持 `REAL_DEVICE_PENDING`，不写设备配置；保留当前分支，不 Push、Merge、Release，不访问旧 NetConsole 真实数据根。
