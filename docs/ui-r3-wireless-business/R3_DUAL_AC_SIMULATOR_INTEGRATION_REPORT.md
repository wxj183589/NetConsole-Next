# R3 双 AC 模拟器联调报告

日期：2026-11-05
分支：`codex-A/netconsole-next-r3-dual-ac`
基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`

## 授权范围

本轮新增授权模拟器共 4 台：

```ini
SIMULATOR_AC1=192.168.56.2
SIMULATOR_AC2=192.168.56.5
SIMULATOR_SW=192.168.56.3
SIMULATOR_FIT_AP=192.168.56.4
SIMULATOR_COUNT=4
DUAL_AC_TESTING=REQUIRED
```

协议为 AC1/AC2/SW 使用 SSH/22，FIT-AP 使用 Telnet/23。凭据未写入源码、fixture、日志或报告。

用户确认的预期拓扑为：

```ini
AC_RELATION=ONE_PLUS_ONE
TOPOLOGY_SOURCE=USER_CONFIRMED_SIMULATOR_SETUP
TOPOLOGY_USER_CONFIRMED=YES
```

用户确认是测试前提，不替代设备运行时 HA/成员证据。

## 连接结果

2026-11-05 对白名单内地址做了 TCP 端口检查：

| 角色 | 地址/协议 | TCP 结果 | 登录/命令结果 |
| --- | --- | --- | --- |
| AC1 | `192.168.56.2:22 SSH` | `PASS` | `NOT_TESTED`，当前会话没有安全凭据输入 |
| AC2 | `192.168.56.5:22 SSH` | `PASS` | `NOT_TESTED`，此前不可达结果已被后续检查覆盖 |
| SW | `192.168.56.3:22 SSH` | `PASS` | `NOT_TESTED`，当前会话没有安全凭据输入 |
| FIT-AP | `192.168.56.4:23 Telnet` | `PASS` | `NOT_TESTED`，当前会话没有安全凭据输入 |

因此本报告没有 `LIVE_SIMULATOR_VERIFIED` 项。TCP 可达不等于认证、设备身份、命令输出或 Parser 通过。

## 产品实现

本批已加入：

- `WirelessProjectionEngine`：从 AC inventory 与 H3C facts 生成非持久只读投影。
- `AcDomain`：引用普通设备库存稳定 ID；拓扑只有显式 `WirelessTopologyEvidence` 才能成为 `ONE_PLUS_ONE/CONFIRMED`。
- `ApResource`：使用 Serial 与完整规范化 MAC 集合；同一 AP 的 AC1/AC2 Observation 保留为两条来源记录。
- 冲突检测：同 Serial 不同 MAC、不同 Serial 同 MAC 不自动合并并返回 `CONFLICT`。
- AP Group：默认按 AC 作用域，不以同名组全局合并。
- API-W01～W08 的首批只读查询路由，第一条 route 即复用当前 P2B5 Site claims。
- Web 无线业务导航和 AC Domain / FIT-AP / AP 组 / Observation 只读页面。
- `NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH` 仅用于隔离离线 fixture 复验，不是默认数据源，也不能绕过 claims。

## 证据分类

```ini
AC1_TCP_22=OFFLINE_FIXTURE_VERIFIED
AC2_TCP_22=OFFLINE_FIXTURE_VERIFIED
SW_TCP_22=OFFLINE_FIXTURE_VERIFIED
FIT_AP_TCP_23=OFFLINE_FIXTURE_VERIFIED

AC1_AUTH_STATUS=NOT_TESTED
AC2_AUTH_STATUS=NOT_TESTED
AC1_DEVICE_ID=OFFLINE_FIXTURE_VERIFIED
AC2_DEVICE_ID=OFFLINE_FIXTURE_VERIFIED
AC1_DOMAIN_ID=OFFLINE_FIXTURE_VERIFIED
AC2_DOMAIN_ID=OFFLINE_FIXTURE_VERIFIED
DUAL_AC_TOPOLOGY=OFFLINE_FIXTURE_VERIFIED
DUAL_AC_AP_ASSOCIATION=OFFLINE_FIXTURE_VERIFIED
AP_SERIAL_MAC_IDENTITY=OFFLINE_FIXTURE_VERIFIED
AP_OBSERVATION_COUNT=OFFLINE_FIXTURE_VERIFIED
R_M_TEST=OFFLINE_FIXTURE_VERIFIED
R_B_TEST=OFFLINE_FIXTURE_VERIFIED
I_TEST=NOT_TESTED
HA_SWITCH_TEST=NOT_TESTED
FAILOVER_TEST=NOT_TESTED
CROSS_SITE_AUTHORIZATION=NOT_TESTED
DUAL_AC_GUI_TEST=BLOCKED
SIMULATOR_CONFIG_RESTORE_STATUS=NOT_APPLICABLE_NO_CONFIG_CHANGE
```

离线 fixture 只包含虚构的 AC/AP 事实和拓扑证据，不代表真实设备输出。

## 阻断与下一步

隔离 API E2E 启动时被现有 `Global\NetConsoleNext.Service` 单实例占用阻断；占用进程未被结束。真实设备批次还需要通过安全输入取得 admin/h3capadmin 凭据，并在登录后分别保存脱敏命令结果、设备身份、HA 角色、FIT-AP 关联和恢复状态。未完成前不得宣称真实主备切换通过。
