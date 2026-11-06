# UI/UX 验收报告

日期：2026-11-06。状态：PASS（已执行的本机浏览器范围），整体 UI 验收仍 PARTIAL。

1920×1080、2560×1440 fixture 已检查登录、概览、设备、终端历史/回放、任务、工具、文件、未知路由和禁用模块；任务重试实际发送请求。历史截图路径 `build/fd-p3-01-ui-1920`、`fd-p3-01-ui-2560` 为 Workspace 内可重新生成证据，不能假设仍保留。

当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`：Vue源文件在恢复/transport退役批次未修改；沿用34b524d前端证据：typecheck、Vitest `14 files / 124 tests`、Vite 构建 PASS；旧 `8 files / 23 tests` 属于 a3954a5 连接批次。网络终端完整 E2E PASS；Linux HTTP/WebSocket 与 Edge 浏览器 PASS，覆盖输入、中文、ANSI、resize、重连与会话关闭；Site Jump Settings Chrome 浏览器 PASS，覆盖表单、留空保留、明确清除、测试/指纹操作、Site 切换、ADMIN 只读、明暗主题及无横向溢出。Jump 浏览器使用 API Mock，实际转发证据来自独立回环 E2E。

新增 H3C VLAN Chrome 源码UI/API mock 6/6、CLI诊断下载 Chrome 源码UI/API mock 8/8 PASS，1920×1080 / 2560×1440截图已检查；诊断包含真实浏览器TXT/ZIP保存与权限/迟到响应隔离。证据分别为 Workspace `build/h3c-vlan-browser/acceptance.json`、`build/task-diagnostics-browser/acceptance.json`；浏览器模拟API不代表后端或设备验收，TasksView定向20/20 PASS。

Windows 原生 125% 显示缩放仍待验收；浏览器视口或 deviceScaleFactor 不替代原生显示测试。跨机 Web/TLS、完整工作台及目标机安装仍待完成，不能据此宣称全部 UI 或部署验收通过。

本批Netmiko有界恢复和旧网络transport退役属于后台范围，不增加浏览器兼容结论；恢复后端证据见 `../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`。最终源码检查点9fc499f；完整终端浏览器session87828 EXIT0，terminal UI contract3 checks PASS；精确SHA离线候选七项PASS，用户Vite PID `90348` 保留。

终端最终回归：`terminal_ui_contract.py` 3 checks PASS；完整终端浏览器 E2E session `87828` EXIT 0，覆盖 Netmiko Telnet 自动认证、SSH / Telnet / AUTO、中文、COM、split/search/theme、ticket/history/replay/export 和 PTY resize；均为本机合成设备 / 浏览器范围。

当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。
