# SSH 自动信任与轮换报告

日期：2026-11-06。状态：PASS（本机服务、worker 与回环 SSH 验证）。

网络设备、Linux 服务器和 Site Jump Host 均在认证成功后自动记录首次指纹及变化，不弹 SSH 确认窗口。网络设备的旧 STRICT 档案不再形成阻断策略；`AUTO_TRUST`、`AUTO_REPLACE`、`AUTO_UPDATE`、`AUTO` 统一采用自动行为。

设备、Linux 与 Jump 指纹分别保存，登录密码独立管理。Catalog 处理并发原子更新和历史审计；配置变化期间的旧连接结果不能覆盖新状态。Jump 回环转发测试覆盖认证后轮换，Linux HTTP/WebSocket 覆盖指纹并发与重启。

自动信任不提供独立设备身份验证。Telnet 不使用 Host Key，保留明文传输提示。真实硬件、实际 Linux 和堡垒机仍 `REAL_DEVICE_PENDING`。
