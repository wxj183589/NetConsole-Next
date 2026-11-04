# 模拟器报告

证据类型：LIVE_SIMULATOR_EVIDENCE；日期：2026-11-05。

| 设备 | 协议/账号 | 连接 | 自动认证 | 运行事实 |
| --- | --- | --- | --- | --- |
| AC1 192.168.56.2 | SSH/22 admin | PASS | auth_none要求publickey/password；自动本机密钥/Agent无可用凭据 | EVIDENCE_PENDING |
| AC2 192.168.56.5 | SSH/22 admin | PASS | auth_none要求publickey/password；自动本机密钥/Agent无可用凭据 | EVIDENCE_PENDING |
| SW 192.168.56.3 | SSH/22 admin | PASS | publickey/password/keyboard-interactive；自动本机密钥/Agent无可用凭据 | EVIDENCE_PENDING |
| FIT-AP 192.168.56.4 | Telnet/23 h3capadmin | PASS，收到banner | 未提供密码；未猜测、未认证 | EVIDENCE_PENDING |

EXPECTED_TOPOLOGY=ONE_PLUS_ONE；RUNTIME_TOPOLOGY_STATUS=EVIDENCE_PENDING；HA_VERIFICATION=NOT_RUN；FAILOVER=NOT_RUN。没有设备身份验证就不进行配置/重启/切换。

SIMULATOR_CONFIG_CHANGES=0；RESTORE_STATUS=NOT_APPLICABLE；UNAUTHORIZED_DEVICE_ACCESS=NO；NETWORK_SCAN_OUTSIDE_WHITELIST=NO。

OFFLINE_FIXTURE_EVIDENCE：双AC同一物理AP去重、独立来源、R/M与R/B、MAC规范化、候选/冲突、重复投递、多来源master、错误采集保留旧证据、局部Site可见性和分页均在单元/浏览器fixture中验证。它们不是四台设备的实时输出。
