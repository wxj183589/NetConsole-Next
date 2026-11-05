# 全天候自主开发日志

## 2026-11-05 07:05 CST

- 集成 `614bbd4` 设备/UI 工作流，产品提交 `7b667da`。
- 新增多命令任务计划：顺序执行、每条命令独立 Raw、失败/取消时保留 partial 证据、schema v16 命令输出账本、Raw→Facts→Current collection provenance。
- 新增 `/api/system/tasks/{runId}/commands` 和任务中心命令明细；设备列表提供一键测试连接，复用服务端 DPAPI 引用与自动 HostKey/Telnet 策略。
- 验证：Service build、Tests build、H3C task fact tests `11/11 PASS`、前端 `vue-tsc --noEmit`、Vitest `4 files/7 tests`。
- 浏览器离线 fixture：登录、明暗主题、设备、终端、任务、工具、文件截图 PASS；证据目录为临时可重生成输出，已退役。
- 四台授权模拟器仍缺可用认证材料；未猜测或记录密码，未宣称真实登录/HA/切换。
- 最新恢复提交：`b5380e0`。

## 2026-11-05 07:45 CST

- 完成 FD-P2-01：新增受 Site claims 约束的 Ping/TCP/DNS/LLDP 批量诊断服务；每个设备/类型独立子任务，批次内最大 4 路并发，支持取消、进度、失败隔离和 128 批次有界保留。
- 新增 `/api/system/diagnostics/runs` 创建、读取、取消接口；LLDP 只读取带 captured scope 的 H3C Facts，不生成拓扑。
- Tools 页面加入设备多选、诊断类型选择、进度和明细；不接收任意地址，不接触凭据。
- 验证：`NETWORK_DIAGNOSTICS_TESTS=3/3 PASS`、完整 Service harness（隔离 TEMP）PASS、Vue typecheck/Vitest `4 files/7 tests`/Vite build PASS。
- 真实四台模拟器仍只有 TCP/协议可达；缺安全凭据，未执行认证或配置写入。
- 本批产品提交：`83e550a`；未 push/merge/release。

## 2026-11-05 Checkpoint 0

基线：产品 worktree `next-autonomous-full-feature`，HEAD=`2343e6b`，主仓库 HEAD=`27ebfb8`，DevStatus=`c4a33e7`。产品和交接工作树均干净。历史 worktree 与提交保留，未做 reset、clean、删除或远端操作。

已完成基线能力：无线 AP Runtime Current/PreviousEffective/Recent10、单目标 Task actor/Site/endpoint scope、当前 session 与全局文件/ZIP 撤权、事实修订单调性与 capturedScope、单命令 Raw→Facts 发布。

本阶段并行工作流：任务调度与任务中心、设备连接和 SSH 自动信任、设备管理与 UI；主线负责多命令采集边界、跨模块集成和最终验收。

外部阻断：四台模拟器只有 TCP/协议可达，缺可用认证材料；HTTP/Service-backed Gate 受现有 Service Mutex 影响。不得猜密码或绕过 Mutex。

下一检查点：合并独立 worktree 中可验证提交，运行后端/前端/浏览器回归，更新任务池和剩余阻断。

## 2026-11-05 08:55 CST

- 完成 FD-P2-02：配置备份允许命令、批量有界调度、版本化脱敏文件、manifest 哈希、历史/差异/下载 API 与配置备份页面；重新校验当前 Site claims，不把配置发布到 Facts。
- 完成诊断历史持久化：已完成批次原子写入 Next 数据根 `diagnostics`，服务对象重建后恢复终态历史，最多保留 128 批次；未完成批次不伪造恢复。
- 验证：`CONFIG_BACKUP_TESTS=1/1 PASS`、`NETWORK_DIAGNOSTICS_TESTS=4/4 PASS`、完整 Service harness PASS、Release build 0 warning/0 error、`vue-tsc --noEmit` PASS、Vitest `4 files / 7 tests PASS`、Vite build PASS、`git diff --check` PASS。
- 更新旧测试中的配置命令拒绝断言，使其覆盖新增只读配置备份命令与实际非法 `reboot` 命令。
- 四台模拟器仍缺安全凭据；真实认证、双 AC HA、切换和实时 AP 归属继续 BLOCKED；未修改生产或开发真实数据，未 push/merge/release。

## 2026-11-05 FD-P3-02

- 新增统一前端 module registry：Router、AppShell 导航和 workspace 标签共用路径、名称、图标及 `READY`/`DISABLED` 状态；移除重复导航定义。
- 新增未知路由和异步模块加载失败页面，保留目标路径与错误详情并提供返回运行总览操作；不为未接入模块生成模拟业务数据。
- 验证：`vue-tsc --noEmit` PASS；Vitest `5 files / 9 tests PASS`；Vite build PASS（既有大 chunk warning）；Web UI contract `3 checks PASS`；`git diff --check` PASS。
- 1920/2560 交互验收、service-backed browser gate 和四台模拟器凭据登录仍待执行；本轮未访问真实设备或真实数据，未 push/merge/release。

## 2026-11-05 FD-P3-01 / Task Center

- 1920×1080 与 2560×1440 离线浏览器验收通过：登录、概览、设备、终端历史、任务中心、工具、文件、未知路由和禁用模块状态均完成交互检查；任务中心“重试”实际发出服务 API 请求。
- 新增任务中心取消、失败重试、服务重启中断恢复 API/UI，服务端复用既有 scheduler，并在每条 route 执行 SUPERADMIN 与当前 Site scope 校验。
- 验证：完整 Service harness PASS；Service/Tests build PASS；`CONCURRENCY_P1_TESTS=4/4 PASS`；Vitest `5 files/10 tests PASS`；vue-tsc、Vite、Web contract `4 checks`、1920/2560 browser PASS。
- 四台模拟器仍缺安全凭据，真实登录、双 AC HA、切换/恢复和实时 AP 归属继续 BLOCKED；未修改真实数据，未 push/merge/release。

## 2026-11-05 R3 Wireless API contract audit

- 扩展 `tests/r3_dual_ac_api_e2e.py` 离线双 AC API 验收：覆盖未知来源 AC 过滤不泄露、完整 MAC 格式校验、runtime-state 隐藏来源返回 404、observations 隐藏来源返回空分页。
- 验证：`R3_DUAL_AC_API_FIXTURE=PASS`、identity dedup、observation provenance、topology evidence、group scope、`R3_WIRELESS_FILTER_SCOPE=PASS`、auth guard 全部 PASS。
- 该证据仍来自离线 fixture；四台模拟器凭据、双 AC HA 切换与实时 AP 归属继续 BLOCKED，未修改真实数据。
- 2026-11-05 收口：全天候 workstream 已合入当前 R3 分支，集成提交 `db8120e`。Release Service build（0 warnings/0 errors）、完整 Service harness、Vue typecheck、Vitest 5 files/10 tests、Web UI contract、Web PR2 contract、R3 wireless API fixture（含 filter scope）均 PASS。四台模拟器凭据仍不可用，实时认证与双 AC HA 继续 BLOCKED。
- 白名单连接性复核：`192.168.56.2:22`、`192.168.56.5:22`、`192.168.56.3:22`、`192.168.56.4:23` 均 TCP 可达；这只证明端口可达，不等同于凭据认证或双 AC HA 验收。
- Service-backed browser smoke：真实隔离 Service 在发布输出目录启动，浏览器完成登录并访问概览、设备、任务、工具、文件页面，`1920×1080 PASS`；首次发现并修复了测试脚本错误地以仓库根作为 ASP.NET content root 的问题，随后复验通过。
- 2026-11-05 模块治理：新增 schema v17 `module_registry`，服务端提供 `/api/system/modules` 与 SUPERADMIN-only 启停 API；核心模块禁止停用，状态重启后保留，停用模块 API 统一返回 404，AppShell 使用服务端状态覆盖导航。`module_registry_e2e.py` 的 ADMIN 403、核心保护、API 门禁、重启持久化全部 PASS。四台模拟器凭据仍不可用，未访问真实设备。
- 模块治理回归：`dotnet build` 0 warning/0 error、Vue typecheck、Vitest `5 files / 10 tests`、模块 API 隔离 e2e 全部 PASS；并行启动两个真实 Service 首次触发单实例 Mutex，改为串行后 Service-backed browser smoke 1920×1080 PASS。
- 完整 .NET Service harness：通过（vendor replay、Raw/Facts、P2B migrations、wireless/runtime、task scope、interactive scope、fact publication、concurrency、SQLite、诊断、归档和性能场景）；模块治理新增 API e2e 仍通过。
- 2026-11-05 R3 持久化：新增 schema v18 无线身份投影表，全球 scope 下以事务写入 AC Domain、稳定 AP Resource、跨 AC Observation 和 AC-owned AP Group；重复投影使用稳定主键幂等更新，隔离重启验收通过。`WIRELESS_PROJECTION_TESTS=PASS`、`WIRELESS_RUNTIME_TESTS=13/13 PASS`、完整 Service harness PASS。
- 2026-11-05 跨 AC collection manifest：新增 schema v19 `wireless_collection_manifests` 与 item provenance，按 source revision 将多 AC observation、Raw manifest ref、AC/domain/Site 和 captured time 绑定；重复读取幂等，隔离重启保留，未复制 Raw 或秘密。
- 2026-11-05 最终复核：提交 `1c54d38` 补齐 SUPERADMIN 模块治理设置页、浏览器“系统设置/模块开放状态”验收和版本化发布资产。串行 `dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release -m:1` 为 0 warnings / 0 errors；随后完整 Service harness PASS（vendor replay、Raw/Facts、P2B migrations、wireless/runtime、schema v19、task scope、interactive scope、fact publication、concurrency、SQLite、诊断、归档和性能场景）。未 push、未 release、未修改真实数据；四台模拟器认证与双 AC HA 继续 BLOCKED。
