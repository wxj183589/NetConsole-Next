# 完整测试矩阵

日期：2026-11-06。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）；整体 PARTIAL。下表中的本机或模拟证据不等于真实硬件兼容。

| 门禁 | 当前结果 | 证据边界 |
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
