# 双 AC 观察与状态历史报告

更新时间：2026-11-14（Asia/Shanghai）

## 结论

R3 已完成“同一物理 AP 的双 AC 观察保留、按来源计算当前状态、聚合状态和有效变化历史”的离线/代码级闭环；真实双 AC HA/failover 仍未验证，不能由 AP 角色或站点关系推导控制器故障转移结论。

## 观察模型

- AP 身份使用精确 Serial + 规范化 MAC 集合；不同 AC 的 observation 作为独立 source 保留。
- 每个 source 独立保留 Current、PreviousEffective、LatestOutcome、FreshnessDeadline 和 history status。
- 相同状态刷新 Current，但不制造无效历史；只有有效状态变化进入最近 10 条记录。
- 同一时间点出现成功与失败/歧义 revision 时，不以成功样本覆盖失败证据。
- 多个 source 同时为 `RUNNING_MASTER` 时聚合状态为 `CONFLICT`，并设置 `MULTI_SOURCE_MASTER`，不猜测哪台 AC 是真实主控。
- 聚合历史只有在所有已知 source 都提供新鲜证据后才推进；同一物理 AP 的跨 AC 证据不互相覆盖。
- `LIVE_VERIFIED` 当前证据优先于离线 fixture；fixture 只作为明确标记的历史/辅助证据。

## HA 结论边界

`RUNNING_MASTER`、`RUNNING_BACKUP` 是 AP 侧观察状态，不等于控制器 failover 已发生。当前能力投影保持 `HaVerification=NOT_TESTED`，并明确 `AP_PRIMARY_BACKUP_OBSERVATIONS_DO_NOT_VERIFY_CONTROLLER_FAILOVER`。只有显式、可追溯的 topology evidence 才能提升 HA 结论；不能用同站点、同域、相同 AP 数量或角色字符串推断 HA。

## 验证状态

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 无线分组合同与缓存 | PASS | `WIRELESS_GROUP_CONTRACT_TESTS=PASS`、`WIRELESS_GROUP_CACHE_TESTS=4/4` |
| 精确身份投影与跨 AC observation | PASS | `WIRELESS_PROJECTION_TESTS=PASS` |
| 运行态、冲突、freshness、最近 10 条有效变化 | PASS | `WIRELESS_RUNTIME_TESTS=14/14 PASS` |
| 替换与生命周期边界 | PASS | `WIRELESS_REPLACEMENT_TESTS=3/3`、`WIRELESS_IDENTITY_LIFECYCLE_TESTS=5/5` |
| AC 资源与事实 scope | PASS | `H3C_AC_RESOURCE_TESTS=4/4`、`H3C_FACT_SCOPE_TESTS=10/10` |
| HA/failover/recovery 真实设备验收 | NOT_VERIFIED | 本批未连接双 AC 真实环境 |
| 真实跨采集周期 8–24 小时历史 | NOT_VERIFIED | 未执行长时运行 |
