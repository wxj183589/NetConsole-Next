# 实现矩阵

日期：2026-11-06。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）；整体 **PARTIAL**。

| 能力 | 当前实现与证据 | 当前阶段 | 仍需证据/工作 |
| --- | --- | --- | --- |
| 多设备/多命令任务 | bounded scheduler、设备互斥、命令级 Raw、完整 manifest 成功门禁 | PASS（隔离） | 实际业务批量采集 |
| 重试/取消/恢复 | 直接按 ID 查询，当前 Site 与 SUPERADMIN 复核 | PASS（隔离） | 实机重启/恢复 |
| 网络 CLI 统一连接 | CPython + Netmiko；H3C/ZTE 采集、连接测试、Web Shell 共用入口 | PASS（本机 + HCL） | 真实硬件/型号兼容 |
| 驱动选择 | 明确厂商/平台/SSH/Telnet 映射，未知映射拒绝，无跨厂商回退 | PASS（worker 20 tests） | 映射存在不等于型号已验证 |
| 设备凭据/快速添加 | Profile 密码自动读取；库存/Profile 原子提交；普通 DTO 不回显；ACL | PASS（API/UI） | SQLite 字段明文；不宣称数据库加密 |
| SSH 自动信任/轮换 | 认证后首次/变化自动更新，旧 STRICT 采用自动行为，配置并发校验 | PASS（本机/回环） | 实际设备互操作 |
| Site Jump Host | Site 独立密码/指纹，Netmiko/Paramiko direct-tcpip，Telnet 不套用 | PASS（回环转发 + Chrome Settings） | 实际堡垒机兼容 |
| 网络 Web Shell | 同一 Netmiko worker，resize、重连、取消、录制/回放 | PASS（完整 E2E） | 真实设备兼容 |
| Linux 服务器终端 | 独立 inventory、SSH.NET ShellStream、WS、中文/ANSI/resize/重连 | PASS（HTTP/WS + Edge） | 实际 Linux；不继承 Site Jump 策略 |
| ZTE 凭据/API/UI | 新增、空白保留、明确清除、自动采集凭据 | PASS（本机） | REAL_DEVICE_PENDING |
| Windows 离线组合包 | fresh Vue、CPython、Netmiko 与锁定依赖，隔离执行 H3C/Linux | PASS（本机候选运行） | 目标机安装/SCM/升级/卸载 |
| AP identity / 双 AC | exact/唯一 MAC 受控关联、冲突 Observation、Current/Recent10 | PARTIAL | 正式 candidate/replacement、Domain/group 生命周期和 HA |
| FIT-AP | 依 AC 管理与采集，不独立连接 | 当前架构约束 | live ownership/业务证据 |
| Raw→Facts→Current | 单/多命令封存、完整性与 collection provenance | PASS（隔离） | 实时多 AC 业务端到端 |
| Site 授权/撤权 | 当前 claims 与 captured scope 交集，文件/终端/任务复核 | PASS（本机回归） | 跨机 Web/TLS |
| 设备管理 | Site/分组/CSV/批量、快速添加、连接测试及 ZTE 入口 | PASS（本机 API/UI） | 实际库存兼容 |
| 诊断/备份/拓扑 | 受控诊断、脱敏备份/差异/下载、LLDP 历史 | PASS（隔离 API/UI） | 完整实时业务验收 |
| 模块治理 | schema v17、SUPERADMIN Settings、核心保护、重启保留 | PASS（隔离 API/UI） | 剩余完整模块工作台 |
| R3 持久化 / manifest | schema v18/v19、AC/AP/Observation/group、provenance、过滤/详情 | PASS（隔离 API/UI） | live 多 AC 和 HA |
| UI / Web | 1920/2K、网络终端、Linux、Jump 页面本机浏览器通过 | PARTIAL | Windows 原生 125%、跨机 Web/TLS |

当前 HCL AC1/AC2/SW 只读 Netmiko 连接/命令 PASS；旧 SW Exec/FIT-AP 独立登录和 Mutex 争用不再是当前阻断。未执行设备配置变更；实机状态 `REAL_DEVICE_PENDING`。
