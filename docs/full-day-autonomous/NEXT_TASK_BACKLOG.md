# 全天候动态任务池

更新：2026-11-06。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）。**整体 PARTIAL**；COMPLETED_LOCAL 仅表示列出的本机范围。真实设备/主机/堡垒机仍 `REAL_DEVICE_PENDING`。不 Push、Merge、Release。

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
| FD-P2-02 | Backup | 配置备份、版本、差异与授权下载 | P2 | Raw/archive/claims | COMPLETED_LOCAL | CONFIG_BACKUP_TESTS=1/1 PASS（隔离） |
| FD-P2-03 | Topology | LLDP 关系与变化历史 | P2 | verified facts | COMPLETED_LOCAL | H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS（隔离） |
| FD-P3-01 | UI | 1920/2560 页面交互 | P3 | UI workstream | COMPLETED_LOCAL | 浏览器视口 PASS；Windows 原生 125% 另项待验收 |
| FD-P3-02 | Integration | module registry/navigation/error states | P3 | frontend build | COMPLETED_LOCAL | typecheck/browser PASS；剩余完整工作台未完成 |
| FD-P2-04 | Integration/Security | 模块持久化、SUPERADMIN 启停和核心保护 | P2 | schema v17/WebAuth | COMPLETED_LOCAL | API/重启/Settings 浏览器 PASS |
| FD-P1-09 | Wireless/Facts | AP identity、AC Domain、跨 AC Observation、AP Group 持久化 | P1 | schema v18 | COMPLETED_LOCAL | 隔离持久化/重启 PASS；不代表正式生命周期 |
| FD-P1-10 | Wireless/Facts | 跨 AC manifest 与 provenance 绑定 | P1 | FD-P1-09/Raw | COMPLETED_LOCAL | schema v19 manifest/restart PASS |
| FD-P3-03 | Docs | 当前矩阵、回执、历史边界和 handoff | P3 | 最终集成证据/本地提交 | COMPLETED_LOCAL | 2026-11-06 当前证据/功能 SHA/离线候选回填，JSON 与 diff 校验；保留整体 PARTIAL |
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
| FD-P1-26 | Offline | 当前 Vue+CPython 离线候选构建与运行 | P1 | Windows x64/.NET 10 Runtime | COMPLETED_LOCAL | 工具 7 tests；最终候选 PATH-only-System32 H3C/Linux E2E PASS；安装待验收 |
| FD-P3-04 | UI/Deployment | 原生 125%、跨机 Web/TLS、目标机安装 | P3 | 真实 Windows/跨机环境 | PENDING_EXTERNAL | 不得用浏览器缩放或本机候选运行替代 |
