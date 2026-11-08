# R7.6 下一任务池

日期：2026-11-08。检查点基线 `0dd6a14`。仅本地提交/集成，禁止 push、remote merge 和 release publish。九工作流归属见 `docs/r7-6-autonomous/R7_6_WORK_POOL.md`；运行状态见 `AUTONOMOUS_RUN_STATE.json`，使用前重新验证 PID。

| 优先级 | 任务 | 状态 | 下一步/验收 |
| --- | --- | --- | --- |
| P0 | Long Ping 生命周期、历史与 CSV | PASS / SERVICE_E2E | 暂停/恢复/取消、P95、重启历史、Quick/Long 独立分组在 HTTP/双分辨率浏览器通过；不代表远端或多日运行 |
| P0 | 丢包采样的历史证据上下文 | PASS / FIXTURE_E2E | 已部署，Service37、HTTP18、双视口18检查通过；客户端AP仍UNKNOWN，继续权威时间关联 |
| P0 | 最新候选与数据完整性 | PASS / 当前核验 | 新候选PID41856 HEALTHY；SQLite/140Raw/checkpoint/19assets通过；完整离线ICMP仍失败 |
| P0 | AC/AP 无线工作台 | PARTIAL | 当前 AC 只读采集和 AP 历史 CSV 已实现；当前无有效变化，不虚构 HA/变化记录 |
| P0 | 统一故障中心 | PARTIAL | 取消误报已修复；合成权限/持久化测试通过，待现场故障/恢复与复发证据 |
| P0 | SW / FIT-AP 原生接入 | BLOCKED | 获取有效凭据后只读核对；不得重复失败认证、猜密码或把 AC 命令接受当作 AP 登录成功 |
| P0 | 双 AC HA 与移动终端关联 | NOT_TESTED | 切换恢复点、客户端连续性测量、可靠关联事实 |
| P0 | 配置恢复完整闭环 | PARTIAL | 模拟器和预检通过；仍需设备 writer、完整配置回读、业务验证及失败回滚 |
| P1 | 完整候选 Traceroute | PARTIAL | APPHOST/显式 DLL 完整离线门禁均 ICMP_PERMISSION_DENIED；窄诊断不能替代完整门禁 |
| P1 | Windows 部署 / LocalManager | PARTIAL | 离线计划及就绪修复通过；待 elevated SCM、独立主机升级/回滚/卸载 |
| P1 | 原版专业能力迁移 | PARTIAL | 复用事实和历史合同，继续 Mesh/轨交专业报表；旧仓只读参考 |
| P1 | 长稳与性能 | NOT_TESTED | 现有仅 300 秒；补有界无人值守、资源/Raw/队列/重启检查，不能写成 24 小时 |
| P1 | 最终报告与交接 | PASS / CHECKPOINT | 报告、loss专项、状态已更新；DevStatus只新增交接，续跑先核对PID和HEAD |

旧 R6/R6.1 待办由本表替代。历史验收保留在对应日期文档，不能将旧数字或候选身份写作当前验证结果。
