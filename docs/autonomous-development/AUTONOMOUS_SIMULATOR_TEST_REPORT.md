# 模拟器报告

证据类型：LIVE_SIMULATOR_EVIDENCE；日期：2026-11-05。最新逐台自动登录结果见 `AUTO_LOGIN_SIMULATOR_ACCEPTANCE_20261105.md`。

| 设备 | 协议/账号 | 连接 | 自动认证 | 运行事实 |
| --- | --- | --- | --- | --- |
| AC1 192.168.56.2 | SSH/22 admin | PASS | DPAPI 引用自动认证；版本识别成功 | H3C Comware 7.1.064 |
| AC2 192.168.56.5 | SSH/22 admin | PASS | DPAPI 引用自动认证；版本识别成功 | H3C Comware 7.1.064 |
| SW 192.168.56.3 | SSH/22 admin | PASS/PARTIAL | SSH 认证成功；Exec 设备信息命令断开/超时 | 登录成功，设备信息识别失败 |
| FIT-AP 192.168.56.4 | Telnet/23 h3capadmin | BLOCKED | banner 可达；引用密码被设备拒绝 | 未执行命令 |

EXPECTED_TOPOLOGY=ONE_PLUS_ONE；RUNTIME_TOPOLOGY_STATUS=EVIDENCE_PENDING；HA_VERIFICATION=NOT_RUN；FAILOVER=NOT_RUN。没有设备身份验证就不进行配置/重启/切换。

SIMULATOR_CONFIG_CHANGES=0；RESTORE_STATUS=NOT_APPLICABLE；UNAUTHORIZED_DEVICE_ACCESS=NO；NETWORK_SCAN_OUTSIDE_WHITELIST=NO。

OFFLINE_FIXTURE_EVIDENCE：双AC同一物理AP去重、独立来源、R/M与R/B、MAC规范化、候选/冲突、重复投递、多来源master、错误采集保留旧证据、局部Site可见性和分页均在单元/浏览器fixture中验证。它们不是四台设备的实时输出。

Checkpoint J复核：用户追加自动登录授权持续有效；本轮已取得 AC1/AC2/SW 的自动认证阶段证据，但没有双 AC HA、切换、恢复或实时 AP 归属证据。单命令Raw发布、Recent10和Site/ZIP测试均为隔离fixture/可控session证据，不能替代四台完整实时业务验收。
