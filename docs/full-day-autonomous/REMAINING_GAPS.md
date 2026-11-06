# 剩余缺口与验收边界

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。本批功能已本地提交 `a3954a5`，文档以所属 commit 为准（起点 `4dac9fc`）；整体状态 **PARTIAL**。

## 当前已闭环

- 网络设备采集、连接测试与 Web Shell 统一 CPython + Netmiko；显式 SSH/Telnet driver mapping、按 Profile 自动取密码、认证后自动信任/更新 Host Key、取消和连接释放均有本机验证。
- 新增设备和 Profile 同事务保存；ZTE 凭据 API/UI、Linux 服务器与交互终端、Site Jump Host 实际回环转发和 Settings 页面已完成本机集成。
- AC1/AC2/SW 当前 Netmiko 登录及只读命令 PASS；FIT-AP 由 AC 管理，不独立连接。旧 DPAPI/SW Exec/FIT-AP 登录结果只作历史记录。
- 最终 Service build（0 warning/error）、完整 harness、Vue typecheck、8 files/23 tests、worker 20 tests、离线工具 7 tests PASS。网络终端、Linux 最终 HTTP/WebSocket + Edge（含完全静默撤权和 SSH 释放） 和 Jump HTTP/Chrome Settings 门禁 PASS。
- 当前离线候选已在隔离 cwd/数据根、PATH 仅 System32、无系统 Python 覆盖下运行：内置 Netmiko/Paramiko、当前静态页面、回环 H3C SSH 与 Linux WebSocket PASS。该候选不是正式发布或目标机安装验收。

## 仍需实现或验收

1. 双 AC HA、切换/恢复及实时 AP 归属；命令返回不能证明主备关系。
2. 正式 AP candidate/replacement、冲突裁决与 Domain/group 生命周期合同。
3. 剩余完整模块工作台及实时多 AC 业务采集的端到端集成；已有诊断/备份/LLDP fixture 不自动升级为现场业务验收。
4. Windows 原生 125% 显示缩放；现有 1920×1080/2K 视口和浏览器缩放不能替代。
5. 跨机器 Web/TLS；当前服务限 loopback。
6. 目标机安装、SCM、升级/卸载和其他 Windows 环境；候选仍依赖目标机 .NET 10 ASP.NET Core Runtime。
7. 真实 H3C、ZTE、其他型号、Linux 主机和实际堡垒机互操作：`REAL_DEVICE_PENDING`。

## 数据与历史

普通密码为受本机 ACL 保护的 SQLite 明文字段，不使用普通设备 DPAPI 中心。密码不在普通 DTO、URL、命令行、日志、Raw 或报告中回显。未写设备配置，未触碰旧 NetConsole 真实数据根。

历史 Mutex 争用已通过串行测试协调解决；当前完整 Service harness 与浏览器门禁通过，不再作为产品阻断。旧测试失败仍保留于历史日志。
