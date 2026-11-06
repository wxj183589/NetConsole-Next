# 剩余阻断

日期：2026-11-06。整体状态：**PARTIAL**。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。

## 已关闭的本机整合项

- VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。
- 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。
- 当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。
- FD-P1-34 / FD-P2-05 为 `COMPLETED_LOCAL`。本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

- 8320e2f 批次历史证据（不代表当前候选/测试数量）：最终Release Service/Tests构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，配置备份6组保留独立入口。
- 8320e2f 批次历史证据（不代表当前候选/测试数量）：当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选只作历史；当前候选通过本机隔离验收，候选不是正式发布。

## 仍需实现或验收

1. 双AC HA、切换/恢复和AP实时归属尚无验收证据；当前AC1/AC2/SW Netmiko只读连接/命令PASS不能代替这些业务门禁。
2. 正式AP candidate/replacement与Domain/group生命周期、完整模块工作台仍需推进；F10隔离和候选缓存收敛不构成正式生命周期。
3. 原版完整AC命令/稳定组证据、保存/启动配置和其他厂商完整结构化业务仍有缺口。有界发送前恢复已本机验收，范围见 FD-P1-35；旧网络 transport 退役与精确 SHA 离线候选已通过本机门禁。 一次性 `display diagnostic-information` 全机诊断命令未迁移；任务封存 TXT/ZIP 下载不等于原版全机诊断。
4. Windows原生125%显示、跨机器Web/TLS、目标机安装/SCM/升级/卸载尚未验收；当前候选本机隔离运行PASS不替代这些门禁，目标机仍需.NET10 ASP.NET Core Runtime。
5. 真实网络设备、Linux主机和实际堡垒机兼容仍 `REAL_DEVICE_PENDING`，不执行真实设备配置变更。

当前不存在“先取得 DPAPI 引用才能继续”的依赖。普通设备录入用户名/密码后自动关联 Profile 并自动认证，库存和 Profile 原子保存；FIT-AP 通过 AC 管理。历史 SW Exec、FIT-AP 独立 Telnet 和 Mutex 争用保留为历史证据，不再列作当前阻断。

专用批量连接入口缺失已关闭，8320e2f批次证据：定向8/8、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko14 checks PASS；策略6组、worker24/24、共享worker16预算6组PASS。网络终端、Linux HTTP/WebSocket/Edge及Jump Settings Chrome等既有证据仍按原范围有效，历史harness、Jump及旧候选各守当时范围；本轮最终全量31025与完整终端87828 EXIT0，精确SHA候选七项PASS。没有Push、Merge或Release，不把候选称作正式发布。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。
