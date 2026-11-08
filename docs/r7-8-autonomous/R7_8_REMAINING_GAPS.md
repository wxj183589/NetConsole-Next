# R7.8 剩余缺口
整体PARTIAL；不重复实现已提交的活动Ping故障、配置变化事件、提醒、分页和精确设备入口。
| 优先级 | 缺口 / 状态 | 下一项与验收 |
| --- | --- | --- |
| P0 | 完整Host ICMP门禁 FAIL | 最终DLL完整门禁仍权限拒绝，窄DLL探针成功。对照相同候选、token、子进程上下文和安全日志定位；不改全局安全/防火墙或提权绕过 |
| P1 | FIT原生认证 BLOCKED | 复用有效现有凭据，验证exec-console实际生效、原生Prompt/MAC/Serial/Raw；不猜密、不将AC观察当原生身份 |
| P1 | SW认证 BLOCKED | 历史认证失败；仅在有新有效凭据时单次产品只读采集 |
| P1 | REAL_HA NOT_TESTED | 完整前备份、可逆触发和回切、同步AP与实际客户端业务连续性；不将R/M/R/B字段当成功 |
| P1 | SIMULATOR_RESTORE / ROLLBACK BLOCKED | 仍缺传输、设备端长度/hash、原生replace、重连、完整回读、业务及失败回滚闭环 |
| P1 | 模拟器真实故障恢复 NOT_TESTED | 回环TCP监听器12项已验；后续受控模拟器注入，保留故障/恢复/复发与Raw事实 |
| P1 | WINDOWS_SCM NOT_TESTED | 离线计划通过；安装/账户/启动/停止/重启/升级/回滚/卸载保留数据和重启存活待独立目标机 |
| P1 | LONG_RUNNING NOT_TESTED | 300秒健康观察不等于8–24h；使用真实窗口采集资源、队列、Raw、SQLite及退出恢复 |
| P2 | 原版专业迁移 PARTIAL | Mesh/MR、SNMP、吞吐、TCP逐跳路径、专业报表等未齐；见迁移矩阵，不从普通TCP RTT推吞吐 |
| P2 | 权威客户端/AP时间关联 PARTIAL | 无事实保持UNKNOWN；禁止按IP/名称模糊猜测AP归属或实时无线质量 |
| P2 | 大规模性能 PARTIAL | 4并发拓扑读取和分页已测；尚无现场规模、长期负载SLA |
Windows125%明确OUT_OF_SCOPE。所有后续禁止远端Push/Merge/Release；保护原始检查点、19资产、dirty Worktree和未推送提交。
