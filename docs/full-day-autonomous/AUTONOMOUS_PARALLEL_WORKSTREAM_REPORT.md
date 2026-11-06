# 并行工作流报告

## 2026-11-06 当前检查点：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。

VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

VLAN、诊断与有界连接恢复已在当前分支本地集成；旧网络 transport 退役、最终完整门禁与精确 SHA 候选已通过本机验收。

VLAN 仅覆盖已测试 Comware detail 格式；诊断只读取已有封存输出，排除配置命令。HTTP 合成 SSH、隔离 fixture 和 Chrome API mock 各守原证据范围，均不替代真实设备兼容；整体 **PARTIAL / REAL_DEVICE_PENDING**。有界发送前恢复已验收，具体恢复限制见下方 FD-P1-35；不把该证据扩展为任意命令重放。未写设备配置、未访问旧 NetConsole 数据根；保留当前分支，不 Push、Merge、Release。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

以下 8320e2f 及更早记录保留当时提交、测试数量与候选范围；其中“当前/最终”仅指历史时点。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 8320e2f 集成状态（历史）

最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。分支 `codex-A/netconsole-next-r3-dual-ac` 保留，不Push、Merge、Release。AP Group来源身份、候选缓存、F10 conservative隔离与配置备份已各自本机验收。本轮连接策略、资源预算、批量API/UI、Terminal reason修复及文档工作已在共享分支集成；整体 **PARTIAL**。

AUTO/legacy策略6组、worker24/24、Service实例共享worker16预算6组、专用批量定向8/8、完整Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko→合成SSH14 checks均PASS。最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，备份6组为独立入口。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，EXE ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。候选不是正式发布；HA/生命周期/VLAN与完整多厂商业务、原生显示/跨机/安装和实机边界保留，`REAL_DEVICE_PENDING`。

## a3954a5连接批次历史范围

`a3954a5`是早前连接批次本地提交，其Service harness、Vue8 files/23 tests、worker20、离线工具7、网络终端、Linux/Jump及离线候选的PASS仅属于该时点。旧候选 `build/service-web-linux-jump-a3954a5-20261106` 不包含后续Group/cache/F10/配置备份及本轮策略/预算/批量入口，不能作为当前候选或最终整合PASS。

## 以下为早期工作流历史记录

已集成工作流：调度/并发（`493cc54`）、连接/HostKey/Telnet（`8a6ab3b`、`e75018a`、`b4aadb7`）、设备/UI（`7b667da`）、主线多命令与事实管线（`b5380e0`）。各工作流使用独立 worktree，最后在产品 worktree 集成。未推送或合并远端。
