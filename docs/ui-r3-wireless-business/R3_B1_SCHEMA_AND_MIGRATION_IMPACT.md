# R3-B1 Schema 与 Migration 影响审计

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
