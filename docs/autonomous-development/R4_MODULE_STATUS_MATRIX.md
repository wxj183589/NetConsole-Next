# R4 Module Status Matrix

日期：2026-11-07；当前 HEAD `2a4d65f`（scheduler、configuration restore、identity lifecycle 已合入）。

| 模块 | 状态 | 当前证据 | 主要缺口 |
| --- | --- | --- | --- |
| A 设备管理 | PARTIAL | 既有设备/Profile/Site/批量 API 与 UI 回归通过 | CSV/资产批量闭环和现场互操作 |
| B 设备连接 | PARTIAL | Netmiko worker、SSH/Telnet、Host Key、任务 ID 查询回归通过 | 四台模拟器认证和真实型号覆盖 |
| C 多任务调度 | PASS_LOCAL | scoped task lookup、完整 Service harness、并发 `6/6`、取消/恢复历史回归 | 重启/权限变化的现场证据 |
| D R3 无线业务 | PARTIAL | projection、observation、AP Group、claims、runtime `13/13` | 完整业务写入和实时多 AC |
| E 双 AC 1+1 | BLOCKED_EXTERNAL | 用户确认拓扑，离线显式 evidence | HA 角色、切换、恢复、AP ownership |
| F AP identity lifecycle | PASS_LOCAL_MUTATION | lifecycle API、isolated JSON authority、ACCEPT/REJECT/CONFLICT/SUPERSEDE、Site/SUPERADMIN/evidence/revision/audit；`5/5` + contract `3/3` | schema-backed production authority、merge/split/revocation and live evidence |
| G 配置备份 | PASS_LOCAL | RUNNING/SAVED/STARTUP_BODY、计划 `15/15`、备份 `11/11` | 真实设备兼容 |
| H 配置恢复 | PARTIAL_SAFE_EXECUTE | preview、差异、兼容/权限边界、dry-run、confirmation token、audit；`CONFIG_RESTORE_TESTS=11/11` | 默认 writer 明确 disabled；真实写入、回滚和设备验收 |
| I 智能配置 | NOT_IMPLEMENTED | 未发现当前产品闭环 | 模板、变量、dry-run 和执行边界 |
| J 网络诊断 | PASS_LOCAL | Ping/TCP/DNS/LLDP/Traceroute `9/9`/`15/15` | 路由/ARP/WLAN/Mesh 业务诊断 |
| K LLDP/拓扑/资产 | PARTIAL | LLDP relation/history 与 Site claims 回归通过 | 完整资产与拓扑工作台 |
| L 权限/审计/数据完整性 | PARTIAL | Site claims、任务审计、Raw provenance | identity/config mutation audit 完整性 |
| M UI/工作台 | PARTIAL | Vitest 17 files/199 tests、typecheck/build | live service/browser、工作台汇总和真实设备态 |
| N 跨机部署 | BLOCKED_EXTERNAL | loopback/offline candidate 历史证据 | TLS、安装、SCM、升级/回滚/卸载 |

本矩阵区分了本地代码证据和外部验收；fixture、mock、合成回环不提升为真实设备 PASS。
