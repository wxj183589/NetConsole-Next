# R3-B0 无线业务正式决策包

日期：2026-11-05
项目：`NetConsole-Next`  
政策：`FROZEN`
冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`

## 2026-11-05 冻结更新

用户批准原文：

> 同意按照修订后的 OPTION_A，正式批准并冻结 R3 的全部 7 项业务决策。

七项当前选择均为 `selectedOption=OPTION_A`、`approvalStatus=APPROVED`、`status=FROZEN`，修订版本为 `20261104-amended`。D04 的修订是 Domain-owned AP Group 必须有明确 domain-shared evidence，否则使用 AC-owned；D06 的修订是有效执行目标取创建 scope snapshot 与当前授权目标的交集，并在受保护边界重检 claims。

冻结只改变业务决策状态，不宣称实现完成：`API_DESIGN=DESIGNED`、`API_IMPLEMENTATION=NOT_IMPLEMENTED`、`DATABASE_MIGRATION=NOT_STARTED`、`R3_UI_IMPLEMENTATION=NOT_STARTED`。2026-11-04 的 amendment、approval sheet 和 readiness 报告仍是批准前历史记录。

## 使用说明

本包把上一轮审计中真实列出的 7 项待决策事项稳定化为 `R3_DECISION_01..07`。2026-11-05 用户已按照修订后的 OPTION_A 正式批准并冻结全部 7 项；本包的 `selectedOption`、`APPROVAL_STATUS` 和 `STATUS` 是当前权威状态。上一轮已确认的大方向继续有效：AC 使用普通设备库存稳定 ID；FIT-AP 不独立创建；无线以 AC 导航；R2 claims 是唯一 Site authority；SmartHub 仅作 clean-room UI 参考。

## 决策总览

| ID | 主题 | 推荐 | 状态 |
| --- | --- | --- | --- |
| `R3_DECISION_01` | AC domain key 与 topology kind | 显式稳定 `AcDomain.id`，引用已有 AC device ID；kind 只由成员/HA/IRF 证据确认 | `FROZEN / OPTION_A` |
| `R3_DECISION_02` | AP serial/MAC identity precedence | serial + 完整 MAC 集合 exact evidence；缺失/冲突进入 candidate/conflict，不猜 | `FROZEN / OPTION_A` |
| `R3_DECISION_03` | 多 AC 下 AP 可见性 | 授权 observation 子集 + provenance；AP 摘要只基于可见事实 | `FROZEN / OPTION_A` |
| `R3_DECISION_04` | AP group ownership/scope | 有明确 domain-shared evidence 才用 Domain key，否则 AC key；只读观察，写入 out of scope | `FROZEN / OPTION_A_AMENDED` |
| `R3_DECISION_05` | R/M、R/B、I 状态 | observation-level normalized state，失败不等同 I，stale/unknown/conflict 分离 | `FROZEN / OPTION_A` |
| `R3_DECISION_06` | Task/report Site scope | snapshot 只限制目标上限；执行、恢复、读取、下载、审计均 intersect 当前 claims | `FROZEN / OPTION_A_AMENDED` |
| `R3_DECISION_07` | Audit provenance | 只引用 Raw manifest/hash/source revision，不写命令输出、凭据或敏感原文 | `FROZEN / OPTION_A` |

## R3_DECISION_01：AC domain key 与 topology kind

- `CURRENT_BEHAVIOR`：AC 是 `device_inventory.device_type=AC` 的普通设备，使用 `device_inventory.id`；当前没有 `AcDomain`/member runtime。证据：`docs/DEVICE_DATA_MODEL.md:5-25`、`src/NetConsole.Next.Service/DeviceManagement.cs:122-157,246-278`、`D:\study\NetConsole-Next-Workspace\architecture-replanning\R0-R1-20261101\04_DEVICE_AND_AC_DOMAIN_MODEL.md:9-35`。
- `PROBLEM`：独立 AC、堆叠、集群和 1+1 不能都用一个普通 device row 表示；但不能复制第二套 AC 资产库存。
- `OPTION_A`：显式生成稳定 `AcDomain.id`，domain 引用一个或多个既有 `device_inventory.id`；`domain_kind` 只有明确成员/HA/IRF/cluster evidence 才能从 `STANDALONE/STACK/CLUSTER/ONE_PLUS_ONE` 中确认。
- `OPTION_B`：每个 AC device 永远隐式等于一个 domain，拓扑只由页面临时推断。
- `OPTION_C`：以排序后的 device IDs 计算 deterministic domain key；成员变化会导致 key 迁移，维护复杂。
- `RECOMMENDED_OPTION`：`OPTION_A`。
- `RECOMMENDATION_REASON`：保持 AC 设备稳定身份，同时给逻辑管理域独立生命周期；没有成员证据时宁可 `UNKNOWN`，不把 AP 状态当 topology 证据。
- `DATA_MODEL_IMPACT`：需要未来的 `AcDomain`、`AcMemberDevice` 逻辑对象；本轮不加字段/表。
- `API_IMPACT`：未来 AC domain list/detail/topology API；现有 device inventory API 继续作为资产来源。
- `SECURITY_IMPACT`：domain Site 必须来自已授权 AC Site；不能用 domain 聚合绕过 claims。
- `MIGRATION_IMPACT`：未来需要 isolated migration 和 legacy device-to-domain mapping；当前 `NO`。
- `UI_IMPACT`：无线导航显示 domain，但详情仍能回链 AC device IDs。
- `TEST_IMPACT`：standalone/stack/cluster/1+1/member conflict/partial domain fixture。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_02：AP serial/MAC identity precedence

- `CURRENT_BEHAVIOR`：H3C parser 可产生 `H3cApFact.Serial`、`H3cApAddressFact.MacAddress`，但当前只是 parser payload，没有 AP resource identity service。证据：`src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-204`。
- `PROBLEM`：缺失、变化、冲突、多接口 MAC 和跨 AC duplicate 不能靠名称、MAC 尾号或第一条记录解决。
- `OPTION_A`：serial 与完整规范化 MAC 集合都作为 exact evidence；唯一 serial 可建立候选，完整唯一 MAC 可辅助确认；冲突返回 `CONFLICT`，缺字段保留 `CANDIDATE/UNKNOWN`。
- `OPTION_B`：MAC 作为全局主键，serial 仅展示。
- `OPTION_C`：serial 作为全局主键，MAC 变化自动更新。
- `RECOMMENDED_OPTION`：`OPTION_A`。
- `RECOMMENDATION_REASON`：兼容设备更换、缺字段和多接口事实，保留可审计证据，不把单一厂商字段绝对化。
- `DATA_MODEL_IMPACT`：未来 `PhysicalApIdentity`、identity evidence/candidate/conflict、MAC role set；当前不落库。
- `API_IMPACT`：AP resource/identity detail 需返回 identity status、evidence、conflict reason。
- `SECURITY_IMPACT`：identity 查询必须先过 AC observation Site authorization，不能用 serial/MAC 作为越权搜索入口。
- `MIGRATION_IMPACT`：未来需离线 backfill/candidate review；当前 `NO`。
- `UI_IMPACT`：显示 confirmed/candidate/conflict/unknown，禁止伪装成稳定 AP。
- `TEST_IMPACT`：7 个 identity scenarios（同/异 serial-MAC、缺字段、更换、迁移）。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_03：多 AC 下 AP 可见性

- `CURRENT_BEHAVIOR`：R2 claims 可过滤 device/profile/facts，但不存在 AP resource/observation visibility API。证据：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`、`Program.cs:918-928`。
- `PROBLEM`：同一 AP 被 SITE_A 的 AC-A 与 SITE_B 的 AC-B 观察时，不能因一个授权自动泄露另一个 Site 的 observation/state/group。
- `OPTION_A`：返回用户有权访问的 observation 子集和 provenance；AP identity 摘要只从可见 observation 投影，未授权 observation 不参与聚合。
- `OPTION_B`：返回全局 AP identity 摘要，但隐藏未授权 AC 细节。
- `OPTION_C`：只要存在任一未授权 observation 就隐藏整个 AP。
- `RECOMMENDED_OPTION`：`OPTION_A`。
- `RECOMMENDATION_REASON`：满足运维人员查看授权 AC 的需求，同时避免跨 Site 状态/组信息泄露；响应可明确 `PARTIAL_SCOPE`。
- `DATA_MODEL_IMPACT`：observation/binding 必须带 source AC、source Site、relation revision。
- `API_IMPACT`：所有 AP list/detail/group/state endpoint 都必须按 observation predicate 过滤。
- `SECURITY_IMPACT`：服务端执行 `AP_IDENTITY_VISIBILITY`、`AC_AP_OBSERVATION_VISIBILITY`、`AP_RUNTIME_STATE_VISIBILITY`、`AP_GROUP_VISIBILITY` 四层判断。
- `MIGRATION_IMPACT`：未来需要 relation/source Site backfill；当前 `NO`。
- `UI_IMPACT`：显示可见范围摘要和 provenance，不显示未授权 AC 名称/状态。
- `TEST_IMPACT`：单 Site、多 Site、无授权、SUPERADMIN、跨 Site duplicate 和 aggregate no-leak。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_04：AP group ownership/scope

- `CURRENT_BEHAVIOR`：parser 的 `H3cApFact.Group` 是字符串字段，没有 group resource/API；无线 UI disabled。证据：`Phase9VendorParsers.cs:160-197`、`web/src/components/AppShell.vue:31`。
- `PROBLEM`：同名组可能存在于不同 AC；同一 Domain 的成员 AC 也可能各自观察到局部组；UI 导航不能证明 Domain-owned。
- `OPTION_A`：组是 AC/domain 侧只读观察。只有明确 domain-shared evidence 才允许 `GROUP_SCOPE=DOMAIN`，稳定 key 为 `DOMAIN|ac_domain_id|source_group_key`；具体 AC 或证据不足时使用 `GROUP_SCOPE=AC`，稳定 key 为 `AC|source_ac_device_id|source_group_key`。缺 source key/ID 则保留 candidate/unknown，不合并；平台不下发修改。
- `OPTION_B`：平台维护全局 AP group，AC 只作为成员来源。
- `OPTION_C`：AC observed group + 平台 local alias 双层模型。
- `RECOMMENDED_OPTION`：`OPTION_A`，并明确 `AP_GROUP_WRITE=OUT_OF_SCOPE`。
- `RECOMMENDATION_REASON`：符合只读事实边界，避免全局 name join；Domain scope 只由证据授权，默认不把同 Domain 的 AC 局部组自动合并。
- `DATA_MODEL_IMPACT`：未来 AC/domain-scoped group resource、identity key、membership projection；当前不实现。
- `API_IMPACT`：只读 group list/detail/members API，响应需带 scope、source IDs、source key、source revision；写接口不进入 B0/B1。
- `SECURITY_IMPACT`：组继承 source AC/domain Site claims；未授权成员不影响 count/state/conflict，客户端不能传入 group scope。
- `MIGRATION_IMPACT`：未来需要 source group key/revision mapping；当前 `NO`。
- `UI_IMPACT`：显示 group scope 和来源；不能用导航层级证明 Domain-owned。
- `TEST_IMPACT`：新增 SC-36..SC-41，覆盖同名、domain-owned/AC-owned、缺失来源、source key/name 变化、成员变化和跨 AC membership。
- `SCHEMA_IMPACT`：`NO_B0_SCHEMA_CHANGE`；逻辑字段只作为未来实现输入。
- `SECURITY_CONDITIONS`：`P2B5_FROM_FIRST_WIRELESS_ROUTE`、`NO_UNAUTHORIZED_GROUP_COUNT`、`NO_CLIENT_SCOPE_OVERRIDE`。
- `APPROVAL_STATUS`：`APPROVED`。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_05：R/M、R/B、I 状态

- `CURRENT_BEHAVIOR`：parser 可返回原始 AP `State`；`h3c_facts` 只有通用 `status/source/captured_ms/is_current`，没有 wireless normalized state/freshness。证据：`Phase9VendorParsers.cs:160-161`、`H3cFacts.cs:8-30,45-123`。
- `PROBLEM`：采集失败不等于离线；多 AC 主备冲突、切换期间和 stale 不能用最新字符串覆盖。
- `OPTION_A`：保留 raw state，归一化为 `RUNNING_MASTER/RUNNING_BACKUP/OFFLINE/UNKNOWN/STALE/CONFLICT`；失败与离线分开；Current 只接受新鲜、验证通过的 observation。
- `OPTION_B`：按 captured time 最新值胜出。
- `OPTION_C`：任何状态冲突都只显示 UNKNOWN，人工确认后再恢复。
- `RECOMMENDED_OPTION`：`OPTION_A`。
- `RECOMMENDATION_REASON`：兼顾可用性和 fail-closed 解释；冲突可见、失败不破坏上一有效事实。
- `DATA_MODEL_IMPACT`：observation/raw/normalized/freshness/confidence/verification 字段；当前不落库。
- `API_IMPACT`：runtime state 返回 raw/normalized/source/time/age/confidence/conflict。
- `SECURITY_IMPACT`：状态聚合只基于授权 observation，不能用未授权 AC 的 R/M 修正当前结果。
- `MIGRATION_IMPACT`：未来需要 facts projection/history migration；当前 `NO`。
- `UI_IMPACT`：区分在线、离线、未知、过期、冲突和部分授权。
- `TEST_IMPACT`：任务要求的 8 个 SCENE 场景全部进入矩阵。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_06：Task/report Site scope

- `CURRENT_BEHAVIOR`：现有 task definition/run 是单 profile/单 command；`GET /api/system/tasks` 为通用任务历史，当前没有 wireless subject predicate。证据：`Program.cs:659,953-989`、`Catalog.cs:1325-1450,1649-1685`。
- `PROBLEM`：任务运行期间 claims 变化时，既要保留创建时可重现的目标上限，又不能让旧 revision 变成永久授权或通过 generic Task/Report/Audit 旁路泄露。
- `OPTION_A`：任务创建时记录 `task_id`、actor、scope snapshot、claims revision、authorized subjects、source AC/Site IDs 和 created time；snapshot 只限制最大目标集合。实际执行目标为 `CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS`，在 connect、command/item、retry、resume、report publish 及读取/下载/审计时重新检查当前 claims。撤销前未开始 item 标记 `REVOKED`；不可安全中断的当前 bounded operation 只能完成原子边界或安全中止，封存 partial result，标记 `REVOKED_AFTER_START`，不得启动后续操作。
- `OPTION_B`：每个阶段动态读取当前 claims，claims 变化立即改变运行目标，但不保留创建时目标上限。
- `OPTION_C`：任务不记录 Site scope，完全依赖 profile role。
- `RECOMMENDED_OPTION`：`OPTION_A_REQUIRES_REVISION`。
- `RECOMMENDATION_REASON`：保留创建时可重现边界，同时把 current claims 作为实际执行和读取授权；新增 Site 不扩大旧任务，部分撤销只显示仍授权结果。
- `DATA_MODEL_IMPACT`：未来 task plan/run scope snapshot、claims revision、source subject、per-item revocation/outcome；当前不改现有 task schema。
- `API_IMPACT`：无线 plan/detail/list/result/report/download/audit API 必须返回 snapshot/current status 并在 server-side 做 subject predicate；generic `/api/system/tasks` 不能旁路。
- `SECURITY_IMPACT`：每个受保护边界重新检查 claims；已撤销 Site 的结果、记录、状态、数量、来源和 hash 不再读取或聚合。
- `MIGRATION_IMPACT`：未来需 task child/item scope migration；当前 `NO`。
- `UI_IMPACT`：显示 creation scope、current scope、`FULL/PARTIAL/REVOKED`，不显示被撤销 Site 详情。
- `TEST_IMPACT`：新增 SC-42..SC-53，覆盖创建前/执行前/执行中/完成后撤销、不可中断边界、新增授权不扩展、恢复、取消 race、revision-only change、generic bypass。
- `SCHEMA_IMPACT`：`NO_B0_SCHEMA_CHANGE`；snapshot/item 字段只作为未来实现输入。
- `SECURITY_CONDITIONS`：`CURRENT_CLAIMS_AT_EVERY_PROTECTED_BOUNDARY`、`NO_NEW_TARGET_EXPANSION`、`READ_DOWNLOAD_AUDIT_FAIL_CLOSED`。
- `APPROVAL_STATUS`：`APPROVED`。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## R3_DECISION_07：Audit provenance

- `CURRENT_BEHAVIOR`：现有 audit 记录 bounded actor/operation/outcome，设备审计明确排除密码、凭据和原始 CSV；没有无线 relation/state 事件合同。证据：`docs/DEVICE_AUDIT_MODEL.md:1-32`。
- `PROBLEM`：无线冲突裁决和状态刷新必须可追溯，但不能把命令输出或凭据写入 audit。
- `OPTION_A`：audit 只保存 action、actor、AC/AP/domain IDs、Site claim revision、observation/relation revision、Raw manifest ID/hash 和 reason code。
- `OPTION_B`：把 bounded raw command output 摘要直接放入 audit detail。
- `OPTION_C`：只记录成功/失败，不保存 provenance reference。
- `RECOMMENDED_OPTION`：`OPTION_A`。
- `RECOMMENDATION_REASON`：可从 audit 回到不可变 Raw/manifest，同时维持现有敏感字段不落日志的边界。
- `DATA_MODEL_IMPACT`：未来增加 wireless audit vocabulary/subject references；当前不改 audit schema。
- `API_IMPACT`：wireless audit query/filter 需按 Site/AC/AP authorization 过滤。
- `SECURITY_IMPACT`：避免 raw、credential、command text 泄露；审计查询不能成为跨 Site side channel。
- `MIGRATION_IMPACT`：未来可能需要 audit detail version/vocabulary migration；当前 `NO`。
- `UI_IMPACT`：显示来源引用、时间、操作人和安全 reason code，不显示敏感原文。
- `TEST_IMPACT`：audit visibility、no-secret leakage、cross-site denial、hash/reference integrity。
- `USER_APPROVAL_REQUIRED`：是。
- `STATUS`：`FROZEN`。

## 统一正式审批字段

以下字段与 JSON 决策对象逐项对应；`selectedOption=OPTION_A` 是冻结后的正式选择，D04/D06 的修订语义见上文和冻结基线：

| DECISION_ID | SELECTED_OPTION | SECURITY_CONDITIONS | SCHEMA_IMPACT | APPROVAL_STATUS |
| --- | --- | --- | --- | --- |
| `R3_DECISION_01` | `OPTION_A` | domain Site 必须来自 authorized AC members | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_02` | `OPTION_A` | identity lookup 先过 source AC Site predicate | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_03` | `OPTION_A` | 不返回全局摘要或未授权 provenance | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_04` | `OPTION_A_AMENDED` | P2B5 from first group route；scope 不由客户端传入 | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_05` | `OPTION_A` | state aggregate 只使用当前授权 observations | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_06` | `OPTION_A_AMENDED` | current claims recheck 和 revocation boundary | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |
| `R3_DECISION_07` | `OPTION_A` | audit 不含 raw/credential，保留 provenance reference | `NO_B0_SCHEMA_CHANGE` | `APPROVED / FROZEN` |

## Amendment 同步规则

- `R3_DECISION_04` 的推荐仍为 `OPTION_A`，但正式描述增加 Domain-owned/AC-owned 双层 scope、source key 和 evidence gate。
- `R3_DECISION_06` 的历史推荐标记为 `OPTION_A_REQUIRES_REVISION`；冻结选择是修订后的 `OPTION_A`，增加 current claims intersection、撤销时序和不可安全中断边界。
- R3-B1 从第一条无线资源 API 即执行 P2B5 server-side authorization；R3-B5 只负责高级跨 AC/partial/no-leak 闭环。
- API-W09 改为内部 P2B5 predicate，不新增 `/api/system/wireless/site-claims` route。

## 已明确的批准与未批准边界

| 事项 | 当前状态 | 依据 |
| --- | --- | --- |
| AC 继续使用普通设备库存稳定 ID | 已确认方向 | 本任务 Phase 3、R2 设备数据模型和上一轮审计 |
| FIT-AP 不独立创建 | 已确认方向 | `DeviceManagement.cs:352,557`、R2 回归 `FIT_AP_CREATE_BLOCKED` |
| R2 Site claims authority 不新建第二套 | 已确认方向 | P2B5/R2 31/31 回归 |
| 7 项具体 identity/topology/visibility/group/state/task/audit 规则 | 已批准并冻结 | 2026-11-05 用户批准记录；冻结基线 `NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1` |

## 用户一次性确认模板（冻结前历史模板）

```text
R3_DECISION_01=OPTION_A/B/C/DEFER
R3_DECISION_02=OPTION_A/B/C/DEFER
R3_DECISION_03=OPTION_A/B/C/DEFER
R3_DECISION_04=OPTION_A/B/C/DEFER
R3_DECISION_05=OPTION_A/B/C/DEFER
R3_DECISION_06=OPTION_A/B/C/DEFER
R3_DECISION_07=OPTION_A/B/C/DEFER
```

该模板记录冻结前的确认格式；当前 7 项均为 `OPTION_A`，状态为 `APPROVED/FROZEN`。历史批准前报告中的 `NEEDS_USER_DECISION` 数值保持原样，不在本文件之外回写。

