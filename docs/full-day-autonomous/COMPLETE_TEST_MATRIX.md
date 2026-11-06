# 完整测试矩阵

2026-11-06 当前集成检查点：功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`，文档以所属提交为准。本轮 AUTO/legacy 策略、共享 worker 预算、专用批量连接、Terminal reason 修复和当前离线候选均已完成本机最终验证。整体 **PARTIAL**，实机保持 **REAL_DEVICE_PENDING**。

| 门禁 | 当前证据 | 证据边界 |
| --- | --- | --- |
| Release Service / Tests build | PASS，0 warnings / 0 errors | 最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` |
| 默认完整 Service harness | session `69905`，EXIT 0 PASS；Terminal reason 最终修复后 | 含策略6组、共享worker预算6组、批量连接8组及既有AP Group/cache/F10/runtime/Raw/并发/迁移/崩溃恢复；配置备份6组保留独立入口 |
| 配置备份定向 harness | CONFIG_BACKUP_TESTS=6/6 PASS | 完整行序/重复次数、超过16384行、大输入分配预算、目标预检/权限与既有备份回归 |
| Web typecheck / Vitest | PASS；12 files / 82 tests PASS | 包含配置备份与专用批量连接；批量专项17 tests，加既有设备详情定向23/23 PASS，不替代后端连接验证 |
| 配置备份 Chrome UI | 6 scenarios PASS | 1920×1080 DPR1；源码 UI + API mock，不表示现场采集 |
| 配置备份 HTTP→Netmiko | CONFIG_BACKUP_HTTP_NETMIKO_E2E=PASS (19 checks) | 实际 HTTP Service→CPython/Netmiko→合成回环 SSH，非真实设备兼容 |
| AP Group 来源身份/schema24 | 合同10场景、迁移/回滚、HTTP16、浏览器15 PASS | fda2a68；HTTP为隔离Service+离线fixture，浏览器为API mock |
| AP Group 候选缓存 | WIRELESS_GROUP_CACHE_TESTS=4/4 PASS | 2696390；仅可重建缓存收敛，不代表业务 ENDED |
| F10 conservative 更换隔离 | WIRELESS_REPLACEMENT_TESTS=3/3 PASS | 764d764；真实隔离SQLite、离线command output与fact: provenance；未生成实时Raw分段 |
| Wireless projection / runtime | Projection/Group合同 PASS；Runtime13/13 PASS | F10与缓存定向回归及完整harness；不代表正式mutation或HA |
| Netmiko AUTO/legacy策略 | H3C_CONNECTION_POLICY_TESTS=PASS (6 groups)；worker24/24 PASS | 显式允许AUTO仅协商/连接超时一次Telnet尝试；认证/HostKey/Jump/取消等不回退；H3C legacy限定条件一次兼容尝试，无通用命令重放 |
| Service实例共享worker预算 | NETMIKO_WORKER_BUDGET_TESTS=PASS (6 groups, shared limit 16) | 真实本机合成子进程；采集/单台测试/网络WebShell/Jump探测共享，等待可取消，进程实际退出后归还；不跨Service实例 |
| 专用批量连接harness | DEVICE_CONNECTION_TESTS=8/8 PASS；已纳入默认完整harness | 精确库存UUID、404优先、409零写入、权限/取消、关联重绑及映射完整性；最终完整Service回归PASS |
| 专用批量连接Chrome | 4 groups PASS | 真实Chrome源码Vue/Vite + 业务API mock；创建/刷新/取消、Site迟到隔离、ADMIN隐藏及1920/2560视口 |
| 专用批量连接HTTP→Netmiko | guard后最终复验14/14 PASS | 实际HTTP Service→CPython/Netmiko→合成loopback SSH；凭据/指纹、双目标报告、权限/模块、取消释放，不代表真实设备 |
| 本轮最终整合/提交 | PASS；功能提交 `8320e2f0653c0f295700883c1900eef0a0690632` | Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码 |
| 当前离线候选 | `OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS | 路径 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`；ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups；候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留 |

本轮提交：`fda2a68` → `2696390` → `764d764` → `e12d062` → `8320e2f`；文档以所属提交为准。详见 `../AP_GROUP_SOURCE_IDENTITY_ACCEPTANCE_20261106.md`、`../AP_GROUP_CACHE_ACCEPTANCE_20261106.md`、`../AP_REPLACEMENT_F10_ACCEPTANCE_20261106.md`、`../CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md`、`../NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md`、`../DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md`。没有设备配置写入，未访问旧NetConsole数据根，不Push、Merge、Release。候选本机运行通过不替代目标机安装或实机兼容验收。

## a3954a5 连接批次历史验收

下表保留原批次结果。其“最终/当前”均只指该历史时点；候选未包含后续 Group/cache/F10/配置备份及最新协商补强，不作为当前候选 PASS。未改动能力的既有证据继续按原范围有效。


日期：2026-11-06。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）；整体 PARTIAL。下表中的本机或模拟证据不等于真实硬件兼容。

| 门禁 | 当时结果 | 证据边界 |
| --- | --- | --- |
| Service Release build | PASS，0 warning/error | 最后 scope 修复后 |
| 完整 Service harness | PASS | 含 ACL/Linux/凭据/Jump、原子 onboarding、任务/Raw/权限回归；旧并发重试失败已由当前通过结果替代 |
| Vue typecheck / Vite | PASS | 当前 Vue 源码 |
| Vitest | 8 files / 23 tests PASS | 页面/组件及既有回归 |
| Netmiko worker | 20 tests PASS | driver、交互、错误、超时/取消边界 |
| 离线工具回归 | 7 tests PASS | 打包 4 + 候选组合 3 |
| 网络终端完整 E2E | PASS | loopback SSH/Telnet/AUTO、中文、PTY resize、重连、分屏、COM、ticket、历史/回放/导出 |
| Linux 最终 HTTP/WebSocket + Edge（含完全静默撤权和 SSH 释放） | PASS | loopback SSH；CRUD、密码更新语义、中文/ANSI、控制键、resize、重连、多会话、Site/撤权、录制、重启/idle/delete；静默撤权 HTTP 回归 PASS |
| Site Jump 转发集成 | PASS | `ssh_jump_host_e2e.py`：真实回环 bastion + Netmiko；两 Site、指纹轮换、采集/WebSocket、并发释放/取消、错误、Telnet 绕过、重启/删除 |
| Site Jump Settings Chrome | PASS | `ssh_jump_host_browser_e2e.mjs`：API Mock；保存/留空/清除、probe、指纹、Site 切换、ADMIN 只读、主题/溢出 |
| ZTE 凭据 API/UI | PASS | 本机 HTTP 与组件；真实 ZTE 待验收 |
| 最终离线候选运行 | PASS | fresh Vite + 内置 CPython，独立 cwd/Production 隔离数据，PATH 仅 System32；内置 Netmiko 4.7.0/Paramiko 4.0.0、静态 hash、回环 H3C SSH/Linux WS 五项 PASS；ProductVersion 含完整 a3954a5；进程/本轮临时目录残留 0 |
| 当前 HCL Netmiko 模拟器 | AC1/AC2/SW PASS | 只读版本/AP/接口/LLDP 命令；FIT-AP AC 管理；配置写入 0 |
| 既有任务/Raw 专项 | PASS | 并发 5/5、Raw 完整 manifest 13/13、历史任务直接 ID 查询 |
| 诊断/备份/LLDP | PASS（隔离） | 诊断 4/4、配置备份 1/1、LLDP 2/2；不从连接命令测试推定全部实时业务验收 |
| R3 无线持久化/API/UI | PASS（隔离） | schema v18/v19、Observation、group/provenance、manifest scope/filter、重启恢复 |
| 模块治理/Service-backed browser | PASS（隔离） | SUPERADMIN 启停、核心保护、API 门禁、重启；已有页面导航与设置页 |
| 1920×1080 / 2560×1440 | PASS（已执行浏览器场景） | 视口不替代原生 125% |
| 双 AC HA/切换/恢复/实时 AP 归属 | PENDING | 不从登录成功推断 |
| Windows 原生 125% / 跨机 Web TLS | PENDING | 当前 loopback |
| 目标机安装/SCM/升级/卸载 | PENDING | framework-dependent，需要 .NET 10 ASP.NET Core Runtime |
| 实际网络设备/Linux/堡垒机 | REAL_DEVICE_PENDING | 回环与 HCL 不替代实机 |

HCL 证据：Workspace `reports/netmiko-simulator-20261106.json`。最终离线候选：Workspace `build/service-web-linux-jump-a3954a5-20261106`。无生产/旧真实开发数据写入；未 Push、Merge、Release。
