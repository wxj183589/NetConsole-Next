# R3-B0 多 AC 关联与 AP 运行状态模型

状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。本文件定义观察、可见性和状态投影，不实现采集或 schema。

冻结更新：D03/D05 已按修订后的 `OPTION_A` 冻结；授权 observation 子集是唯一聚合输入，状态规范化不等于产品实现。

## 1. AC↔AP observation

每一条 AP observation 至少包含：

```text
observation_id
ap_resource_id (nullable while candidate)
source_ac_device_id
source_ac_domain_id
source_site_id / site_claim_revision
observed_name
serial_evidence / normalized_mac_set
raw_state
normalized_state
freshness: FRESH | STALE | UNKNOWN
captured_ms / received_ms
raw_manifest_ref / parser_revision
relation_status: ACTIVE | ENDED | CANDIDATE | CONFLICT
```

同一 AP 被多个 AC 观察时，每个来源保留独立 observation；禁止把 source AC、Site、group 或 state 覆盖成单值。关联投影只使用用户当前可见的 observation 子集，符合 `R3_DECISION_03=OPTION_A` 的建议。

## 2. 可见性与聚合规则

| 条件 | AP 资源响应 | observation | state/group 聚合 |
| --- | --- | --- | --- |
| 所有来源 Site 已授权 | 返回 | 返回可见全部 | 可聚合 |
| 仅部分来源 Site 已授权 | 返回 `PARTIAL_SCOPE` | 只返回已授权来源 | 只基于可见来源 |
| 没有来源 Site 已授权 | 404 或 scope denied，按现有 API 语义 | 不返回 | 不计算 |
| SUPERADMIN 全局作用域 | 返回全量当前 authority 内事实 | 返回全量 | 可聚合但仍保留 provenance |
| claims revision 失效 | 不使用旧授权扩大读取 | 重新解析当前 claims | fail closed |

聚合结果必须标注 `source_observation_ids`、`source_count`、`scope_status` 和 `relation_revision`。不允许用“隐藏未授权字段但返回全局 AP 摘要”的方式建立跨 Site side channel。

## 3. 运行状态规范化

raw state 永远保留；规范化映射由厂商/parser profile version 维护：

| raw 例值 | normalized 建议 | 额外条件 |
| --- | --- | --- |
| `R/M` | `RUNNING_MASTER` | 需匹配当前 AC HA 语义 |
| `R/B` | `RUNNING_BACKUP` | 需匹配当前 AC HA 语义 |
| `I` | `OFFLINE` 或 `UNKNOWN` | 只有厂商 profile 明确 `I=inactive` 才可为 OFFLINE |
| 空值/未识别 | `UNKNOWN` | 不猜测 |
| 证据超过 freshness deadline | `STALE` | 不覆盖上一有效事实 |
| 两个有效来源互斥 | `CONFLICT` | 返回冲突来源与 reason code |

状态返回建议：

```text
raw_state
normalized_state
state_reason_code
freshness
captured_ms
age_ms
source_observation_ids
confidence: CONFIRMED | DERIVED | UNKNOWN
conflict: null | {kind, sourceObservationIds, detailCode}
```

采集失败、timeout、parser error、权限拒绝和设备不可达是 observation/collection outcome，不自动转换为 `OFFLINE`。失败时保留上一条有效 Current，并把本次失败写入 bounded task/audit outcome。

## 4. 状态冲突与多 AC 主备

- 同一 AC domain 内 `RUNNING_MASTER` 与 `RUNNING_BACKUP` 同时存在时，按 domain topology evidence 判断是否为正常 HA 成对状态；不能直接把 AP 置 offline。
- 同一 AP 在不同 AC/domain 上给出不同 normalized state 时，返回 `CONFLICT` 或 `PARTIAL_SCOPE`，同时保留各来源；不得按 captured time 简单覆盖。
- source AC/domain 处于 `UNKNOWN` 或 relation `CONFLICT` 时，派生状态最高只能为 `UNKNOWN`/`CONFLICT`。
- 未授权来源不能参与冲突消解，即使它的时间更新。
- `Current + PreviousEffective + Recent10` 只对有效状态变化生效；无变化刷新保留 Current/timestamps，不制造变化样本。

## 5. 拓扑模型

拓扑由 `AcDomain` 和其 member AC device IDs 表达；AP association 只建立到 source domain/observation。推荐支持：`STANDALONE`、`STACK`、`CLUSTER`、`ONE_PLUS_ONE`、`UNKNOWN`。成员证据不足、revision 不一致或跨 Site claim 不一致时为 `CANDIDATE`/`CONFLICT`。

不支持：根据 AP 名称推断 domain、跨 Site 自动合并同名 AC、用一个 AC 的 AP state 修正另一个 AC、把设备 group 当作 topology。

## 6. 依据

- `src/NetConsole.Next.Service/H3cFacts.cs:8-30,45-123`：现有 fact current/history 语义。
- `src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-220`：当前 AP/state parser payload。
- `src/NetConsole.Next.Service/BoundedCollectionScheduler.cs:8-16,79-116`：当前单设备、单命令收集边界。
- `docs/ui-r3-wireless-business/R3_B0_BUSINESS_DECISION_PACKAGE.md`：`R3_DECISION_03`、`R3_DECISION_05`。
