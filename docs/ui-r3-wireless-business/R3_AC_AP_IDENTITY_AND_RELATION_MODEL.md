# R3 AC/AP 身份与关联合同

日期：2026-11-04

本文件是合同草案，不是 schema/migration 实施。目标是避免无线页面为了显示数据而复制 AC 身份、用 AP 名称猜测关系，或在多 AC 视图中生成重复 AP。

## 1. 已冻结的身份边界

### AC

AC 仍然是设备管理中的普通网络设备：

```text
AC.device_id = device_inventory.id
AC.site_id   = device_inventory.site_id
AC.profile   = device_inventory.terminal_profile_id -> h3c_device_profiles.id
```

现有 `device_inventory.id` 是稳定设备 ID，不能因为无线页面出现而生成第二个 AC UUID。IP、名称、厂商、型号和 profile host 是属性或连接声明，不是身份重建依据。

### FIT-AP

R3 必须把下列概念分开：

```text
PHYSICAL_AP          = 跨 AC 复用的物理 AP 资产
AP_IDENTITY          = serial/MAC 等经过规范化和证据分级的身份候选/确认结果
AC_AP_OBSERVATION    = 某次某 AC 对某 AP 的观测
AP_RUNTIME_STATE     = 在 observation 上解释出的状态，不等于资产身份
```

当前源码只有 `H3cApFact(Name, Id, State, Model, Serial, Group, OnlineTime)` 和 `H3cApAddressFact(Name, IpAddress, MacAddress)` parser record（`Phase9VendorParsers.cs:160-164`），并没有 `PHYSICAL_AP` resource 或 AC/AP relation 持久化合同。

## 2. 建议的逻辑对象

以下只是 R3 实施前的逻辑合同；本轮不新增表。

| 对象 | 必要字段 | 身份/来源规则 |
| --- | --- | --- |
| `AcDomain` | `id`, `site_id`, `name`, `domain_kind`, `vendor`, `management_profile_id`, `source_revision`, `validity` | 逻辑管理域；必须引用一个或多个已有 `device_inventory.id`，不能替换 AC 资产身份 |
| `AcMemberDevice` | `ac_domain_id`, `device_id`, `role`, `priority`, `member_state`, `observed_at`, `observation_id` | 只有明确 member evidence 才建立 stack/cluster/1+1 关系 |
| `ApResource` | `id`, `identity_status`, `serial_norm`, `mac_norm_set`, `model`, `first_seen`, `last_seen` | 物理 AP 主身份；不以 `ac_id`、名称或 MAC 尾号为主键 |
| `AcApObservation` | `id`, `ac_domain_id`, `source_device_id`, `ap_resource_candidate`, `raw_ref`, `captured_at`, `source_revision`, `verification_level` | 某 AC 某次观测，允许多个 AC 观测同一 AP |
| `AcApBinding` | `ac_domain_id`, `ap_resource_id`, `binding_state`, `valid_from`, `valid_to`, `relation_revision`, `conflict_code` | 关系事实，不把关系当 AP 资产身份 |
| `ApGroupMembership` | `ac_domain_id`, `group_key`, `ap_resource_id`, `observed_at`, `revision` | 组依附 AC/domain，禁止全局按名称合并 |
| `ApRuntimeObservation` | `ap_resource_id`, `observation_id`, `raw_state`, `normalized_state`, `observed_at`, `freshness`, `confidence` | 状态是观测投影，可冲突、可过期、可 unknown |

这些对象与 R0-R1 设计中的 `AcDomain`、`ApResource`、`Observation`、`AcMemberDevice` 方向一致，详见 `architecture-replanning/R0-R1-20261101/04_DEVICE_AND_AC_DOMAIN_MODEL.md:5-39`；它们不是当前 Next 已存在的生产 schema。

## 3. 身份证据优先级

推荐顺序：

1. 已确认的稳定 AP resource ID。
2. 完整、规范化、唯一且在同一证据 revision 中一致的序列号。
3. 完整、规范化且证据等级足够的硬件 MAC 集合；多接口 MAC 必须保留 interface/role，不取尾号。
4. 仅用于候选匹配的厂商型号、管理地址、AP ID、名称、组名和在线时间。
5. 不可用或冲突时返回 `UNKNOWN`/`CONFLICT`，不得用名称模糊合并。

`serial`、`mac` 缺失时的合同：

| 情况 | 允许结果 | 禁止结果 |
| --- | --- | --- |
| 有 serial，无 MAC | 建立待确认 identity candidate；保留 serial evidence | 直接用名称当最终跨 AC identity |
| 有 MAC，无 serial | 只有完整/唯一/证据充分时可关联；标记缺 serial | 用 MAC 尾号或 OUI 唯一化 |
| 多接口 MAC | 保存 MAC 集合和接口角色 | 随意选一个 MAC 覆盖其它证据 |
| serial/MAC 冲突 | `CONFLICT`，保留候选集合和来源 | 静默选择最新/第一个 |
| 同一 AP 被两个 AC 发现 | 一个 AP resource + 两条 observation/binding | 每个 AC 复制一份 AP 资产 |
| 设备更换 | 新 AP resource 或明确 replacement relation | 复用旧 resource 覆盖历史 |

## 4. 关系和授权语义

```text
AC-A (device_id=A) ── observes ── AP1 (ap_resource_id=P1)
AC-B (device_id=B) ── observes ── AP1 (ap_resource_id=P1)
```

同一 AP 的多个 AC 观测必须同时保留：来源 AC、来源 Site、采集 run、captured time、source revision、原始证据引用、解析/验证状态。无线查询先通过 AC/domain 的 Site claims 判断是否可见，再返回 AP resource 以及每条 observation 的 provenance；不能把任意关联 AC 的 Site 自动合并成 `AP_SITE`。

## 5. 当前缺口与验收用例

当前缺口：

- 无 `ApResource`/`AcApBinding` API 或持久化实现。
- 无 serial/MAC normalization/conflict service。
- 无多 AC duplicate、replacement、缺字段和多接口 MAC fixture。
- 无 relation-specific Site authorization test。

最小离线验收用例：

1. 同 serial、不同 AC、不同状态 → 一个 AP resource、两条 observation、状态分开。
2. 同 MAC、serial 缺失 → 进入候选/受控关联，响应明确缺 serial。
3. serial 相同、MAC 冲突 → `CONFLICT`，不覆盖 Current。
4. AP 从 AC-A 迁移到 AC-B → 历史 binding 保留，Current relation 按证据 revision 切换。
5. 一个用户仅有 AC-A Site claim → 只能看到授权 AC 及其允许的 observation，不因 AP 也被 AC-B 发现而泄露 AC-B Site 事实。

