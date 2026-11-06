# R3-B1 Schema 与 Migration 影响审计

## 2026-11-06 当前实施边界

当前 AP Group 来源身份修复为 `LOCAL_ACCEPTANCE_PASS`，由用户持续开发任务覆盖；下方 `DESIGN_ONLY`、`R3_B1_DATABASE_CHANGE_AUTHORIZED=NO` 和“禁止 migration”只记录 B1 准备批次，不构成本次重复授权门槛。冻结设计本身不授予实施权限的原则仍有效。

schema23 已用于 Linux SSH；本切片使用 `storage/schema_v24.sql`，仅重建可丢弃、可重建的 `wireless_ap_groups` projection。持久化内容为可空 source/identity key、identity status、membership revision、实际 observation/source AC/evidence refs、观察时间、冲突原因与 typed source-group observations。Raw/Facts 保持事实 Authority；不引入新的凭据中心或完整无线生命周期表。

旧名称键不能作为确认身份迁移：保留 display/member 信息，降为 `group_scope=AC`、`group_identity_status=CANDIDATE`、`source_group_key=NULL`、`group_identity_key=NULL`，标记 `LEGACY_GROUP_NAME_ONLY`。已用最小隔离 fixture 核验事务失败回滚、幂等/重复执行、fresh/reopen、旧名称行降级与多候选 null-key 共存；Wireless projection 与完整 Service harness 同样通过。当前本机结果为 `LOCAL_ACCEPTANCE_PASS`，不访问旧 NetConsole 真实数据根，不宣称生产数据迁移完成。

完整 AP candidate/replacement、Domain/group 生命周期及其持久历史仍为后续范围。当前合同与场景映射见 [AP Group 合同](R3_B0_AP_GROUP_CONTRACT.md) 和 [测试矩阵](R3_B0_TEST_SCENARIO_MATRIX.md)。

## 历史 B1 准备期审计


基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
审计状态：`DESIGN_ONLY`  
`R3_B1_DATABASE_CHANGE_AUTHORIZED=NO`

## 1. 两种实现路径

| 路径 | 做法 | 优点 | 风险/前置 |
| --- | --- | --- | --- |
| A：非持久只读 projection | 从现有 device inventory、raw manifest、H3C facts、claims 和离线 fixture 生成 typed `AcDomain`/`TypedApResource`/`Observation` 响应 | 无 schema/migration；回滚是禁用 route；不复制真实数据 | 稳定身份、历史、跨采集 revision、Task item 和 audit provenance 不能长期依赖临时 projection |
| B：正式 wireless persistence | 新增 domain/member/AP identity/observation/relation/history/task/audit 结构并迁移或回填 | 可提供稳定 ID、历史、索引和可重放查询 | 需要 schema review、migration/backfill、权限审计、回滚/兼容窗口和独立数据批准 |

## 2. 当前建议

建议 B1 首批采用路径 A，前提是现有 raw/facts 能提供完整 source ID、captured time、revision 和可验证 provenance；只做服务端受 claims 保护的 read-only typed projection。任何字段无法由现有证据安全派生时，应返回 `UNKNOWN/CANDIDATE/CONFLICT` 或阻止实现，不得为了补齐页面而自动建表。

正式 persistence 不是本轮决定。稳定 AP identity、Recent10/历史、跨 AC relation、Task item snapshot 和无线 audit vocabulary 仍可能需要后续 B2/B3/B5/B7 的独立 schema decision。当前机器可读结论为：

```text
R3_B1_DATABASE_CHANGE_REQUIRED=UNDETERMINED_FOR_FUTURE_IMPLEMENTATION
R3_B1_DATABASE_CHANGE_REQUIRED_THIS_PREPARATION=NO
R3_B1_DATABASE_CHANGE_AUTHORIZED=NO
NEXT_DATABASE_SCHEMA_CHANGED=NO
NEXT_DATABASE_MIGRATION_CHANGED=NO
```

## 3. 影响对象审计

| 对象 | B1 非持久 projection | 正式 persistence 的额外影响 |
| --- | --- | --- |
| AC Domain/member | 从已有 AC device IDs 与批准的关系证据投影 | domain/member identity、唯一性、成员变更和 backfill |
| AP identity | 从 serial/MAC exact evidence 投影 candidate/confirmed/conflict | identity lifecycle、replacement、merge review、冲突人工处理 |
| Observation | 从 raw/facts 带出 source AC/Site/captured/revision | observation relation、retention、历史索引和 source revision foreign key |
| Runtime state | 按当前授权 observation 临时聚合 | Current/Recent10/history、stale/conflict lifecycle 和 bounded retention |
| Task scope | B1 不执行 task；只保留 D06 设计 | creation snapshot、item subject、claim revision、revoke state |
| Audit provenance | B1 不新增持久审计写入 | safe vocabulary、manifest/hash reference、访问控制和 retention |

## 4. Migration 与回滚要求（未来）

若未来选择 B，必须单独产出 schema diff、版本号、数据 ownership、mapping/backfill、duplicate/conflict policy、read/write compatibility、rollback/read-disable、observability 和分阶段 dry-run 计划。迁移必须使用最小隔离 fixture 或经授权的 candidate，不得复制整个 Site/数据库，也不得把历史 Legacy HistoryStore runtime 重新引入。

在该授权获得前，禁止新增 migration、执行 migration、写入生产/开发真实数据或将 projection 结果缓存为事实来源。
