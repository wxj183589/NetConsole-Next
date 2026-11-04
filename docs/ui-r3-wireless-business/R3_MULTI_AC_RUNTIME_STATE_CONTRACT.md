# R3 多 AC 运行状态合同

日期：2026-11-05

状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`
冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`

冻结更新：D05 已冻结；本文件定义 raw/runtime state 与 Current/Recent10 边界，未新增运行时实现或 schema/migration。

## 1. 目标

把物理 AP、AC/AP 观测、原始状态和当前投影分开，避免把一条 AC 输出中的 `R/M`、`R/B` 或 `I` 直接解释成全局主备拓扑。当前 parser 只产生原始 `State` 字段（`Phase9VendorParsers.cs:160-161`），本合同尚未在服务端实现。

## 2. 观测模型

每条 `AcApObservation` 至少携带：

```text
observation_id
ap_resource_id / identity_candidate
ac_domain_id
source_device_id
raw_state
normalized_state
captured_at
source_revision
raw_manifest_ref / raw_sha256
parse_status
verification_level
freshness_state
confidence
conflict_code
```

`normalized_state` 只描述该 AC 对该 AP 的观测，不直接描述集群拓扑。建议枚举：

```text
RUNNING_MASTER  # R/M，只有 raw evidence 明确且验证通过时使用
RUNNING_BACKUP  # R/B，只有 raw evidence 明确且验证通过时使用
OFFLINE         # I，只有明确离线/不可服务证据时使用
UNKNOWN         # 未采集、无法解析、状态不明
STALE           # 曾有有效值但超过 freshness deadline
CONFLICT        # 同一关系在有效 revision 内出现不可裁决冲突
```

`I` 不应由“本次没有返回 AP”自动推断；采集失败、权限失败、解析失败和空表必须分别保留。失败不能清除其它来源已经确认的 Current。

## 3. 必须支持的多 AC场景

### 场景 A：同一 AP 的主备观察

```text
AP1 -> AC-A -> raw=R/M -> normalized=RUNNING_MASTER
AP1 -> AC-B -> raw=R/B -> normalized=RUNNING_BACKUP
```

当前 AP 总览可显示“AC-A 观测为主、AC-B 观测为备”，但不能仅凭两行字符串宣布 AC-A/AC-B 已被确认是 1+1 或 cluster。只有 `AcDomain.domain_kind` 和 member evidence 共同确认后，才生成拓扑投影。

### 场景 B：冲突观察

```text
AP1 -> AC-A -> R/M @ t1
AP1 -> AC-B -> R/M @ t2
```

若 domain topology 未确认，显示两条独立 observation，并返回 `CONFLICT` 或 `MULTI_SOURCE_MASTER` reason；不能静默以较新的时间覆盖较旧的事实。

### 场景 C：不同时间的 I

```text
AP2 -> AC-A -> I @ t3
AP2 -> AC-B -> R/M @ t4
```

如果 t4 证据新鲜且来源有效，AP Current 可以显示可运行，但仍保留 AC-A 的离线历史；若 AC-B 采集失败或 t4 过期，Current 应为 `STALE/UNKNOWN`，不能显示在线。

## 4. Current / PreviousEffective / Recent10

按 NetConsole-Next 已冻结的动态事实语义：

- `Current`：每个 AP resource + state dimension 当前最高可信、仍在 freshness window 的有效事实。
- `PreviousEffective`：上一个有效事实；失败、空表、解析失败不成为 PreviousEffective。
- `Recent10`：最近 10 条真实有效变化记录，不是定时采样；原始状态/来源/时间变化但 effective value 未变时不追加变化事件。
- 失败、冲突、权限拒绝和未采集要作为 observation/event 保留，但不能覆盖有效 Current。

当前 `h3c_facts.is_current` + `history=true` 只提供 profile/kind/identity 级事实列表（`H3cFacts.cs:45-123`），尚未提供上述 AP 状态投影和 Recent10 上限。

## 5. 失效与冲突规则

| 情况 | Current | 事件/审计 |
| --- | --- | --- |
| 新鲜、已验证 R/M | `RUNNING_MASTER` | 记录 source/AC/revision |
| 新鲜、已验证 R/B | `RUNNING_BACKUP` | 记录 source/AC/revision |
| 明确离线 I | `OFFLINE` | 保留 raw state 和时间 |
| 采集失败 | 保留上一有效 Current；另记 `COLLECTION_FAILED` | 不改 Current |
| 解析失败/格式未知 | 保留上一有效 Current；observation=`UNKNOWN` | 不猜状态 |
| 超过 freshness deadline | `STALE` | 指向上一有效事实 |
| 同 revision 冲突 | `CONFLICT` | 保留候选 observation，需人工/规则裁决 |
| AP identity 冲突 | 不投影到 AP resource | 进入 identity conflict queue |

## 6. 多 AC 拓扑类型

| 类型 | 必须的证据 | 不能做的推断 |
| --- | --- | --- |
| 独立 AC | 独立 device/domain identity | 不把同 AP 观察合成主备 |
| 堆叠/IRF | 明确成员、优先级、member state | 不以 AP R/M/R/B 代替成员证据 |
| 集群 | domain membership + cluster role/health | 不以相同名字或 IP 猜 cluster |
| 1+1 | 明确 HA/member/peer evidence | 不以一主一备字符串单独确认 |

## 7. 验收要求

R3-B3/B5 至少应有离线 fixture：同 AP 双 AC、状态冲突、时间倒序、采集失败保留 Current、过期、identity conflict、AP 迁移和重复导入幂等。没有这些测试前，`R3_RUNTIME_STATE_CONTRACT` 和 `R3_MULTI_AC_TOPOLOGY_CONTRACT` 不能升为 PASS。

