# R7.2 双 AC 与 HA 运行证据

2026-11-08；codex-A；branch `codex-A/netconsole-next-r3-dual-ac`。状态 **PARTIAL**：新鲜双源观测及 Raw→事实→AP 资源引用通过；HA_SWITCH、受控切换/回切及无线业务恢复均 **NOT_TESTED**。

证据：Workspace `reports\r72-product-dual-ac-20261108-033929\summary.json`。产品任务 AC1 `e7811304d2ff4f1d836e8f9542fd89c3`、AC2 `f5fe079e1f404b72b7363a330aa31cd2` 均 SUCCEEDED，每台六命令 COMPLETE/PARSED，12 段 Raw 逐段完整性验证通过。

本次精确以完整 serial `H3C_4C-48-CB-6E-04-00`、MAC `4C:48:CB:6E:04:00` 匹配一个物理 AP 资源，保留两条独立来源观测：

| 来源 | 原文 / 规范状态 | 资源关联 |
| --- | --- | --- |
| `r68-ac1` / `.2` | `R/M` / RUNNING_MASTER | 引用 AC1 本次 `command.display.wlan.ap.all` 事实 |
| `r68-ac2` / `.5` | `R/B` / RUNNING_BACKUP | 引用 AC2 本次 `command.display.wlan.ap.all` 事实 |

两条观测 Site 均为 `r68-simulator`，引用的 AP 表事实 Raw 已验证，未用模糊名称合并、未使用旧成功替代本次观测。来源端点、Site 和 sourceId 均封存于事实范围；Raw run 来源及捕获时间保留，随时间自然过期。

两台控制器报告 Common；AP verbose 的 Master/Backup、Ready-for-Switchover Ready、CAPWAP data-tunnel Up 支持“存在备份关系”的观测，不能验证实际切换。两台 Radio 1/2 均 Down、客户端 0；没有经验证的无线客户端业务流量。

沿用此前已核实的管理拓扑边界：主机与 AC1 共经 SW GE1/0/1，关闭该口会同时破坏回退管理路径；现有帮助尚未证明一种精确、可恢复且具独立管理回退的手动 AP 切换路径。因此未故障注入、未关闭接口、未调整优先级、未建立新的 AP 配置段。本轮未获得足以改变该准入结论的新证据。

FIT 原生认证仍缺有效密码，本轮未猜测或重试认证；FIT 直接访问与 AC 观测分别记录。配置写入、切换、回切和业务恢复均不计 PASS。产品完成度仍应保留 PARTIAL。

## 下午接续

AC1新run `e8443c6c7d354e10ad34912296990445` TIMED_OUT，探针在该终态停止，没有启动AC2采集。三台白名单SSH端口各一次检查均超时。凌晨双AC成功仅保留为历史观测，当前能力时效会自然变STALE。没有执行HA切换/回切，独立管理回退和业务流量证据仍缺。
