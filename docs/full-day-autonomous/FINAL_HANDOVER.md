# 最终交接

## 2026-11-06 当前集成检查点

Branch：`codex-A/netconsole-next-r3-dual-ac`。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准，整体 **PARTIAL**。保留当前分支，不 Push、Merge、Release。

已完成本机增量：AP Group来源身份 `fda2a68`；FULL授权投影内候选缓存按来源时间水位收敛 `2696390`；F10 conservative更换身份/runtime/离线输出/来源隔离 `764d764`；设备配置备份明确入口与有界保序差异 `e12d062`；Netmiko策略、资源预算、批量连接与Terminal reason修复 `8320e2f`。F10不实现正式replacement link，缓存清理不表示业务ENDED，均不改Raw/Facts历史Authority。

当前证据：Group合同/Projection PASS、Runtime13/13、cache4/4、F10 3/3；备份独立6/6、Chrome mock6场景、实际HTTP→CPython/Netmiko→合成回环SSH备份19 checks PASS。完整Web12 files/82 tests/typecheck PASS。最终Release Service/Tests构建0 warnings / 0 errors。HTTP/fixture/mock与实机证据边界见 `../AP_GROUP_SOURCE_IDENTITY_ACCEPTANCE_20261106.md`、`../AP_GROUP_CACHE_ACCEPTANCE_20261106.md`、`../AP_REPLACEMENT_F10_ACCEPTANCE_20261106.md`、`../CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md`。

本轮Netmiko轻量AUTO/legacy策略6组、worker24/24、Service实例共享16个worker槽的预算6组PASS。专用批量连接API/UI已补齐，明确库存UUID及精确Profile，复用现有计划/调度器/Netmiko，固定只读display version；定向8/8、Chrome mock4组、guard后实际HTTP→Netmiko→合成回环SSH14 checks PASS。限SUPERADMIN、独立H3C；FIT-AP仍经AC管理。Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。批量8组已纳入默认完整harness，配置备份6组保留独立入口。证据见 `../NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md`、`../DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md`。

当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，EXE ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS；STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选只代表历史连接批次；当前候选本机验收通过，不是正式发布，也不替代目标机安装验收。

下一步推进candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销/revision并发与可靠终止合同。VLAN实时CLI及完整多厂商业务、双AC HA/实时归属、真实来源键/共享组、剩余工作台、原生125%、跨机TLS、目标机安装和实机兼容仍待。通用断线重连/退避/命令重放未实现。仅Next Workspace隔离数据，无设备配置写入，未访问旧数据根，保持`REAL_DEVICE_PENDING`。

## 连接批次交接（历史）

以下保留a3954a5当时结果；其中“最终/当前”仅指该时点，不覆盖后续增量或代表当前候选PASS。

日期：2026-11-06。整体：**PARTIAL**。工作树：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`；Branch：`codex-A/netconsole-next-r3-dual-ac`；本批功能已本地提交 `a3954a5e95590782a576272d4e4b3c526eab248b`，文档以所属 commit 为准（起点 `4dac9fc`）。按用户要求保留当前分支，不 Push、Merge、Release。

## 本批结果

网络设备 CLI 统一 CPython + Netmiko，连接测试、采集和 Web Shell 共用驱动/密码/Host Key 策略；轻量封装只负责生命周期与业务边界。普通密码保存到受 ACL 保护的 SQLite 字段，不建 DPAPI 凭据中心；onboarding 同事务保存库存和 Profile。首次或变化指纹认证后自动保存，包括旧 STRICT 档案。ZTE 保存凭据/UI、Linux 服务器终端、Site Jump Host 实际转发及设置页面完成本机验证。

AC1/AC2/SW 当前 Netmiko 自动认证和只读命令 PASS，证据 `D:\study\NetConsole-Next-Workspace\reports\netmiko-simulator-20261106.json`。FIT-AP 由 AC 管理。没有配置写入，未据此声明 HA 或 AP 实时归属。

## 验证

最终 Service build 0 warnings/errors、完整 Service harness、Vue typecheck、8 files/23 tests、worker 20 tests、离线相关 7 tests PASS。网络终端完整 E2E、Linux 最终 HTTP/WebSocket + Edge（含完全静默撤权和 SSH 释放）、Jump 回环转发和 Settings Chrome PASS。后者浏览器使用 API Mock，实际转发由独立 E2E 验证。

最终候选位于 Workspace `build/service-web-linux-jump-a3954a5-20261106`。fresh Vite/CPython 组合构建及 `offline_candidate_e2e.py --candidate ...` PASS：候选 EXE、独立 cwd、Production 模式隔离数据根、PATH 仅 System32、清除 Python/worker 覆盖，验证内置 Netmiko 4.7/Paramiko 4、静态页面 hash、回环 H3C SSH 和 Linux WebSocket。EXE ProductVersion 为 `1.0.0+a3954a5e95590782a576272d4e4b3c526eab248b`，五项 `OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 均 PASS；残留 Service/Python 进程和本轮临时目录均为 0。不是目标机安装/SCM/GUI 证据。

## 继续顺序

读取 `AUTONOMOUS_RUN_STATE.json` 和 canonical 架构文档，重新检查 branch/HEAD/status 与并行任务，保留已有改动。继续正式 AP 生命周期和双 AC HA/归属合同、剩余工作台、Windows 原生 125%、跨机 Web/TLS 与目标机安装。真实硬件、Linux 和堡垒机均 `REAL_DEVICE_PENDING`。不再先索取旧 DPAPI 引用，不独立连接 FIT-AP。

数据影响仅 Next Workspace 内隔离测试、候选和报告；旧 NetConsole 数据根未访问，未复制真实 Site，未写设备配置。测试与当前矩阵见 `COMPLETE_TEST_MATRIX.md`，剩余项见 `REMAINING_GAPS.md`。
