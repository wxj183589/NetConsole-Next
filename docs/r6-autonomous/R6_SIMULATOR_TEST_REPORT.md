# R6.1 模拟器测试报告

白名单端点：AC1 `192.168.56.2:22`、AC2 `192.168.56.5:22`、SW `192.168.56.3:22`、FIT-AP `192.168.56.4:23`。

本轮使用短超时 TCP 探测确认四个端口均可达。这只能证明端口可达，不能证明 SSH/Telnet 认证、设备命令、WLAN/AP 采集或 HA 状态。

本机没有可安全注入的模拟器凭据。没有猜测或穷举密码，也没有连接白名单外地址；因此本轮没有执行认证、配置读取、配置变更、恢复、Failover、AP ownership 或设备写入。已有只读 Netmiko 证据见 `docs/full-day-autonomous/DUAL_AC_ONE_PLUS_ONE_SIMULATOR_REPORT.md` 和 Workspace `reports/netmiko-simulator-20261106.json`，其范围不扩展为本轮实测。

结论：TCP `PARTIAL`；认证/命令 `BLOCKED_EXTERNAL`；双 AC HA、切换/恢复和实时 AP 归属 `BLOCKED_EXTERNAL`；设备配置写入 `0`。
