# Raw → Facts → Current 验收

## 2026-11-06 当前复核：9fc499f

Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。

VLAN `170bf81` 已复用统一Netmiko、命令级Raw manifest与Facts发布，parser6/6、publication14/14 PASS。发布整张 `vlan.table/default` 修订，完整新表替换Current、旧表保留历史；partial/unsupported/empty独立状态，不用规划数据或旧表冒充实采。HTTP合成SSH25 checks与Chrome API mock6场景分别通过，不提升为真实型号兼容。

诊断 `34b524d` 只读取已有封存Raw，验证segment/output双hash、字节数、归属和命令计划；TXT/ZIP脱敏下载单独标记实际下载hash/size，配置输出排除，零新增采集任务/导出文件缓存。后端89/89、HTTP合成SSH21 checks、Chrome mock8/8、离线OFFLINE_TASK_DIAGNOSTICS PASS；HTTP最终大小限制的时间边界见专项验收。

本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。 无设备配置/旧数据写入，真实设备保持 `REAL_DEVICE_PENDING`。

有界恢复中的ZTE撤权/失败把已完成事实封为PARTIAL历史，保留旧Current；重连不会把未完整回显提升为成功。

以下保留a3954a5及原始13/13合同历史；当前完整publication回归为14/14。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。


当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。

## a3954a5 复核（历史）

本批功能已本地提交 `a3954a5`，文档以所属 commit 为准。当前完整 Service harness PASS，以下 Raw/Facts 13/13 隔离合同保持。网络连接路径已统一 Netmiko，当前 HCL 命令返回只证明只读连接与交互，不将此 Raw/Facts fixture 提升为全部业务或实际硬件验收。无设备配置或旧真实数据写入。

## 原实现验收记录

状态：PASS（隔离 fixture）。schema v16 `task_run_command_outputs` 保存命令序号、命令、Raw 路径、hash、capture/parse 状态；多命令按序执行。成功终态现在要求每个计划命令都有独立封存 Raw manifest，缺失命令会 fail closed 且不提交部分 command set。完整集合使用 collection provenance 发布 Facts；任一命令失败或取消时只保留 partial Raw，不发布 Facts。`H3C_TASK_FACT_PUBLICATION_TESTS=13/13 PASS`。
