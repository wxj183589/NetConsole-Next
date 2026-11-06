# 实现矩阵

日期：2026-11-06。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，本轮提交链 `fda2a68` / `2696390` / `764d764` / `e12d062` / `8320e2f`；文档以所属提交为准。整体 **PARTIAL**。最终Release构建0 warnings / 0 errors，Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。

| 能力 | 当前实现与证据 | 当前阶段 | 仍需证据/工作 |
| --- | --- | --- | --- |
| 多设备/多命令任务 | bounded scheduler、设备互斥、命令级 Raw、完整 manifest 成功门禁 | PASS（隔离） | 实际业务批量采集 |
| 重试/取消/恢复 | 直接按 ID 查询，当前 Site 与 SUPERADMIN 复核 | PASS（隔离） | 实机重启/恢复 |
| 网络 CLI 统一连接 | CPython + Netmiko；H3C/ZTE 采集、连接测试、Web Shell 共用入口 | PASS（本机 + HCL） | 真实硬件/型号兼容 |
| 驱动选择与AUTO/legacy | 明确厂商/平台/SSH/Telnet映射，未知映射拒绝；共用轻量协商策略，限定一次Telnet或H3C legacy尝试 | 策略6组、worker24/24、最终完整Service harness及Jump两项PASS | 映射存在不等于型号已验证，不含通用断线重连/命令重放 |
| Netmiko进程资源预算 | 每Service实例共享16个CPython worker槽，覆盖采集/单台测试/网络WebShell/Jump探测；可取消等待，实际退出才归还 | 预算6组（真实合成子进程）、最终完整Service harness及Jump两项PASS | 不跨Service实例；不是SSH连接池；真实设备并发容量待验收 |
| 专用批量连接API/UI | 明确选择1–64库存UUID，先整批可见性、后兼容/精确Profile关联预检；复用任务计划/调度器/Netmiko，展示命令报告与取消 | 定向8/8并纳入最终完整harness、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko14 checks PASS | 限SUPERADMIN及启用的独立H3C，只读display version；FIT-AP仍经AC管理，实机兼容待验收 |
| 设备凭据/快速添加 | Profile 密码自动读取；库存/Profile 原子提交；普通 DTO 不回显；ACL | PASS（API/UI） | SQLite 字段明文；不宣称数据库加密 |
| SSH 自动信任/轮换 | 认证后首次/变化自动更新，旧 STRICT 采用自动行为，配置并发校验 | PASS（本机/回环） | 实际设备互操作 |
| Site Jump Host | Site 独立密码/指纹，Netmiko/Paramiko direct-tcpip，Telnet 不套用 | PASS（回环转发 + Chrome Settings） | 实际堡垒机兼容 |
| 网络 Web Shell | 同一 Netmiko worker，resize、重连、取消、录制/回放 | PASS（完整 E2E） | 真实设备兼容 |
| Linux 服务器终端 | 独立 inventory、SSH.NET ShellStream、WS、中文/ANSI/resize/重连 | PASS（HTTP/WS + Edge） | 实际 Linux；不继承 Site Jump 策略 |
| ZTE 凭据/API/UI | 新增、空白保留、明确清除、自动采集凭据 | PASS（本机） | REAL_DEVICE_PENDING |
| Windows 离线组合包 | 8320e2f fresh Vite + 内置CPython 3.13/Netmiko 4.7/Paramiko 4；Production、独立cwd/隔离数据、PATH仅System32、无Python/worker覆盖 | COMPLETED_LOCAL；五项本机PASS | 目标机仍需.NET10 ASP.NET Core Runtime；安装/SCM/升级/卸载另项验收，候选不是正式发布 |
| AP identity / 双 AC | exact/唯一MAC受控关联、冲突Observation、Current/Recent10；F10新旧身份/运行态/来源隔离3/3 PASS | PARTIAL | candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销/revision并发合同及可靠终止证据仍待；HA另待验收 |
| FIT-AP | 依 AC 管理与采集，不独立连接 | 当前架构约束 | live ownership/业务证据 |
| Raw→Facts→Current | 单/多命令封存、完整性与 collection provenance | PASS（隔离） | 实时多 AC 业务端到端 |
| Site 授权/撤权 | 当前 claims 与 captured scope 交集，文件/终端/任务复核 | PASS（本机回归） | 跨机 Web/TLS |
| 设备管理 | Site/分组/CSV/批量、快速添加、连接测试及 ZTE 入口 | PASS（本机 API/UI） | 实际库存兼容 |
| 诊断/备份/拓扑 | 库存UUID→明确Profile备份入口，保序/重复次数有界diff；6/6、Web65、Chrome6、HTTP→Netmiko19 PASS；诊断/LLDP既有证据保留 | PASS（隔离API/UI/回环） | 当前备份只支持独立H3C；现场与完整实时业务验收仍待 |
| VLAN及完整多厂商业务 | 现有VLAN规划/导入与接口VLAN字段保留 | PARTIAL | 缺生产display vlan/show vlan采集与完整解析；多厂商driver映射不代表完整结构化业务兼容，原版完整AC命令/组证据及CLI诊断导出仍待 |
| 模块治理 | schema v17、SUPERADMIN Settings、核心保护、重启保留 | PASS（隔离 API/UI） | 剩余完整模块工作台 |
| R3 持久化 / manifest | schema18/19事实/manifest；schema24来源Group可重建投影；候选缓存按逐来源时间水位收敛4/4 PASS | PASS（隔离API/UI） | cache退役不删除Raw/Facts/历史Observation，不是完整Domain/group生命周期；live多AC/HA仍待 |
| UI / Web | 1920/2K、网络终端、Linux、Jump 页面本机浏览器通过 | PARTIAL | Windows 原生 125%、跨机 Web/TLS |

当前 HCL AC1/AC2/SW 只读 Netmiko 连接/命令 PASS；旧 SW Exec/FIT-AP 独立登录和 Mutex 争用不再是当前阻断。未执行设备配置变更；实机状态 `REAL_DEVICE_PENDING`。

最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` 的Release Service/Tests构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。本轮策略6组、预算6组与批量8/8分别PASS，批量已纳入默认完整harness；Web typecheck及12 files/82 tests、guard后批量HTTP14/14 PASS。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS，STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。文档以所属提交为准；证据见对应20261106验收文档。
