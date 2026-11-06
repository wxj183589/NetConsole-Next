# 剩余阻断

日期：2026-11-06。整体状态：**PARTIAL**。

1. 双 AC HA、切换/恢复和 AP 实时归属尚无验收证据；当前只完成 AC1/AC2/SW Netmiko 只读连接/命令验证。
2. 正式 AP candidate/replacement 与 Domain/group 生命周期、完整模块工作台仍需推进。
3. Windows 原生 125% 显示、跨机器 Web/TLS、目标机安装/SCM/升级/卸载尚未验收；离线候选本机隔离运行 PASS 不替代这些门禁。
4. 真实网络设备、Linux 主机和实际堡垒机兼容仍 `REAL_DEVICE_PENDING`，不执行真实设备配置变更。

当前不存在“先取得 DPAPI 引用才能继续”的依赖。普通设备录入用户名/密码后自动关联 Profile 并自动认证，库存和 Profile 原子保存；FIT-AP 通过 AC 管理。历史 SW Exec、FIT-AP 独立 Telnet 和 Mutex 争用保留为历史证据，不再列作当前阻断。

当前 Service full harness、网络终端、Linux HTTP/WebSocket/Edge、Jump 回环转发/Settings Chrome、最终离线候选运行门禁已通过。没有 Push、Merge 或 Release；不把构建候选称作发布。
