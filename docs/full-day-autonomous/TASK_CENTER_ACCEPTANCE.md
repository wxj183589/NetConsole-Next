# 任务中心验收

## 2026-11-06 当前复核：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

任务计划新增默认未选的VLAN采集项，继续复用现有scheduler、统一Netmiko与Raw/Facts；命令明细先校验设备当前Site scope再读取，隐藏任务404。VLAN parser6/6、publication14/14、HTTP合成SSH25 checks、Chrome mock6/6 PASS。

CLI诊断下载新增 `GET /api/system/tasks/{runId}/diagnostics` 与 `/diagnostics/download`（可选commandIndex），提供单条TXT/ZIP，只读取封存证据；manifest控制资格，配置输出排除，发送前/流中撤权复核，Blob后再次核验与迟到隔离。后端89/89、HTTP合成SSH21 checks、TasksView20/20、Chrome mock8/8及离线诊断门禁PASS。诊断下载不重连、不重采集。采集有界恢复独立验收，使用原任务期限/取消与实时Site边界，不自动重放发送后未知结果。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。 所列HTTP为合成回环SSH，Chrome为模拟API；真实设备保持 `REAL_DEVICE_PENDING`。无设备配置/旧数据写入；不Push、Merge、Release。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## 任务中心既有验收（历史基线）

以下保留原批次测试数量与API合同；当前完整前端124 tests、publication14/14以上方检查点为准。

日期：2026-11-06。状态：PASS（本机隔离调度/API/UI）。整体业务验收仍 PARTIAL。

任务列表与命令明细读取服务端真实 run/Raw/Facts 状态。固定只读多命令计划复用既有 task-definition/run；命令按序执行、独立 Raw，成功终态需要完整 manifest。任务详情、取消、失败重试、重启中断恢复直接按 run ID 查询，不受最近 100 条窗口限制。

- `POST /api/system/tasks/{runId}/cancel`
- `POST /api/system/tasks/{runId}/retry`
- `POST /api/system/tasks/{runId}/resume`
- `GET /api/system/tasks/{runId}/commands`

动作由服务端先检查 SUPERADMIN 和当前 Site scope，再调用 BoundedCollectionScheduler；不接受客户端指定权限范围。父计划保留目标快照、子 run 关联与部分失败聚合。

当前完整 Service harness、并发 5/5、Raw/Facts 13/13、Vue typecheck、Vitest 8 files/23 tests、HTTP 多命令计划与已执行的浏览器场景 PASS。历史 Mutex 是测试协调记录，串行 service/browser 门禁已通过，不再阻断。

当前 AC1/AC2/SW Netmiko 只读登录/命令通过；FIT-AP 不独立连接。业务任务端到端、双 AC HA 与实际硬件恢复仍需独立证据，不能由连接测试替代。
