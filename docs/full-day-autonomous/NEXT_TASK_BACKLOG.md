# 全天候动态任务池

2026-11-06 当前集成检查点：最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准。AP Group 来源身份 `fda2a68`、候选缓存收敛 `2696390`、F10 conservative 更换隔离 `764d764`、设备配置备份入口与有界差异 `e12d062` 及Netmiko策略/预算/批量连接与Terminal reason修复 `8320e2f` 已完成各自本机验收。整体 **PARTIAL**，不 Push、Merge、Release。

Netmiko轻量AUTO/legacy策略6组、worker24/24、Service实例共享worker上限16的预算6组、专用批量连接定向8/8、完整Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko→合成回环SSH14 checks均PASS。最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选只作历史，当前候选不是正式发布。

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
| --- | --- | --- | --- | --- | --- | --- |
| FD-P1-27 | Wireless | AP Group 来源键、身份与成员版本 | P1 | explicit evidence/Site claims/schema24 | COMPLETED_LOCAL | fda2a68；合同10场景、迁移、HTTP16、浏览器15 PASS；实时来源键/共享证据待验收 |
| FD-P1-28 | Wireless | AP Group 派生候选缓存收敛 | P1 | FULL 授权投影/逐来源时间水位 | COMPLETED_LOCAL | 2696390；缓存4/4 PASS；不删除 Raw/Facts/历史 Observation，不推断组 ENDED |
| FD-P1-29 | Wireless | F10 conservative replacement 隔离 | P1 | 既有 identity/runtime/provenance 合同 | COMPLETED_LOCAL | 764d764；3/3 PASS；独立身份/历史/来源隔离，不代表正式 replacement 写流程 |
| FD-P1-30 | Wireless | 正式 candidate/identity/replacement 写流程与关系终止 | P1 | 权威写入/撤销/revision 并发合同 | CONTRACT_PENDING | 确认/拒绝、合并/拆分、replacement link 写入及可靠终止证据仍待定义，不从缺席推断 ENDED |
| FD-P1-31 | Connection | Netmiko AUTO/legacy 轻量协商补强 | P1 | 共用连接入口/错误分类/取消 | COMPLETED_LOCAL | 策略6组、worker24/24、Terminal reason修复后的最终默认完整Service harness session69905 EXIT0及Jump两项PASS；功能提交8320e2f，不引入第二套SSH/Telnet实现 |
| FD-P1-32 | Connection/Device UI | 专用批量连接API与设备页入口 | P1 | 精确库存UUID/Profile/现有任务计划 | COMPLETED_LOCAL | 定向8/8已纳入最终完整harness、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko14 checks与最终Jump两项PASS；限独立H3C只读版本命令 |
| FD-P1-33 | Connection/Resources | Service实例共享Netmiko worker上限16 | P1 | 进程lease/启动退出/可取消等待 | COMPLETED_LOCAL | 真实合成子进程预算6组、最终完整Service harness session69905 EXIT0及Jump两项PASS；采集/单台测试/网络WebShell/Jump探测共享，实际退出后归还 |

下表为既有连接能力与本轮状态。


连接批次历史基线：2026-11-06，`a3954a5`（起点 `4dac9fc`）；下表保留既有能力，并按本轮证据更新备份、文档和候选状态。worker20与离线工具7 tests是历史批次证据；本轮策略、worker24/24、预算与批量入口结果见上表，不以旧候选替代新候选。**整体 PARTIAL**；COMPLETED_LOCAL仅表示列出的本机范围。真实设备/主机/堡垒机仍 `REAL_DEVICE_PENDING`。不Push、Merge、Release。

| ID | 工作流 | 任务 | 优先级 | 依赖 | 状态 | 验收 |
| --- | --- | --- | --- | --- | --- | --- |
| FD-P0-01 | Security | 凭据、密码、HostKey、Site 权限无泄漏 | P0 | Profile 密码/数据根 ACL/claims | COMPLETED_LOCAL | 最终 Service harness、密码脱敏与撤权回归 PASS |
| FD-P1-01 | Scheduler | 批量子任务、并发上限、同设备互斥 | P1 | BoundedCollectionScheduler | COMPLETED_LOCAL | CONCURRENCY_P1_TESTS=5/5 PASS |
| FD-P1-02 | Scheduler | 多命令顺序、命令级状态、独立 Raw | P1 | Raw manifest 合同 | COMPLETED_LOCAL | H3C fact tests 13/13；缺完整 manifest 拒绝成功终态 |
| FD-P1-03 | Connection | SSH 首次自动信任、指纹轮换和并发更新 | P1 | 认证成功/HostKey Catalog | COMPLETED_LOCAL | 网络/Linux/Jump 回环验证；旧 STRICT 采用自动策略 |
| FD-P1-04 | Connection | 一键连接测试和中文错误分类 | P1 | Profile/Netmiko worker | COMPLETED_LOCAL | API/设备列表与 HCL AC1/AC2/SW PASS |
| FD-P1-05 | Wireless | 双 AC/AP identity、Observation、group 和 runtime | P1 | R3 projection | PARTIAL | fixture PASS；正式生命周期仍需实现，HA/live ownership 待验收 |
| FD-P1-06 | Facts | 多命令 manifest 与 Raw→Facts→Current | P1 | FD-P1-02 | COMPLETED_LOCAL | 13/13 publication tests；隔离 collection provenance |
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
| FD-P3-03 | Docs | 当前矩阵、回执、历史边界和 handoff | P3 | 最终集成证据/本地提交 | COMPLETED_LOCAL | 已同步最终功能提交8320e2f、最终完整harness/Jump与离线候选五项PASS；文档以所属提交为准，整体PARTIAL |
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
| FD-P1-26 | Offline | 当前 Vue+CPython 离线候选构建与运行 | P1 | 最终功能 SHA/Windows x64/.NET 10 Runtime | COMPLETED_LOCAL | 候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；五项本机PASS，Production隔离数据/PATH仅System32/内置CPython运行，进程和临时目录残留0；目标机安装另待验收 |
| FD-P3-04 | UI/Deployment | 原生 125%、跨机 Web/TLS、目标机安装 | P3 | 真实 Windows/跨机环境 | PENDING_EXTERNAL | 不得用浏览器缩放或本机候选运行替代 |
