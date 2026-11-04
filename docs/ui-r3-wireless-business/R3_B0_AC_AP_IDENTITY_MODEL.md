# R3-B0 AC/AP 身份模型合同

状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。本文件是设计合同，不是数据库迁移或运行时实现。

冻结更新：D01/D02 已按修订后的 `OPTION_A` 冻结；API 仍为设计，产品代码、schema 和 migration 均未修改。

## 1. 身份边界

| 对象 | 稳定身份 | 来源 | B0 结论 |
| --- | --- | --- | --- |
| AC 设备 | `device_inventory.id` | 现有设备库存 | 复用，不复制库存 |
| AC 管理域 | `ac_domain_id` | 未来逻辑投影，引用一个或多个 AC device ID | 需用户确认 `R3_DECISION_01` |
| AP 资源 | `ap_resource_id` | 未来无线资源投影 | 不使用 AP 名称或 MAC 尾号作为主键 |
| AP 观察 | `observation_id` | AC、采集时间、原始事实和 parser revision | 每次来源事实可独立追溯 |
| AP 物理身份 | `physical_ap_identity_id` | serial/MAC exact evidence | 不确定时保留 candidate/conflict |

AC inventory 是资产权威；`AcDomain` 只能引用已有 `device_inventory.id`，不能重新创建第二套 AC 资产。FIT-AP 仍不得作为独立设备创建。依据：`docs/DEVICE_DATA_MODEL.md:5-25`、`src/NetConsole.Next.Service/DeviceManagement.cs:352,557`。

## 2. AC domain 投影

建议字段（逻辑合同，非本轮 schema）：

```text
ac_domain_id: opaque stable id
display_name: string
domain_kind: STANDALONE | STACK | CLUSTER | ONE_PLUS_ONE | UNKNOWN
member_device_ids: device_inventory.id[]
source_evidence_refs: RawManifestRef[]
site_claim_projection: SiteClaimProjection
topology_status: CONFIRMED | CANDIDATE | CONFLICT | UNKNOWN
revision: opaque relation revision
```

`domain_kind` 只能由成员、HA、IRF、cluster 或厂商明确事实确认；AP 观测结果不能反推 AC topology。成员不完整或证据冲突时返回 `CANDIDATE`/`CONFLICT`，不静默降级为 standalone。

## 3. AP 资源与物理身份

AP 资源是可授权、可分页、可追溯的无线对象；它不是 parser 输出行的直接别名。

```text
ap_resource_id: stable logical id
physical_ap_identity_id: nullable until confirmed
source_observation_ids: visible observation ids only
identity_status: CONFIRMED | CANDIDATE | CONFLICT | UNKNOWN | REPLACED
display_name: observed name, never identity key
source_ac_domain_ids: visible source domains only
site_scope_status: FULL | PARTIAL | NONE
first_seen_ms / last_seen_ms: evidence times
revision: resource projection revision
```

身份 evidence：

- `serial` 保留规范化值和原始来源引用；空值不是匹配证据。
- MAC 保留完整规范化地址集合和接口角色（management/radio/other/unknown）；不能只存尾号。
- 同一 observation 内 serial、MAC、AC、时间和原始 manifest 组成 evidence tuple。
- 完整唯一 evidence 可以确认；缺字段保留 `CANDIDATE`/`UNKNOWN`；不一致进入 `CONFLICT`。
- 更换设备、跨 AC duplicate、MAC 集合变化不能自动合并；必须产生新的 evidence/review path。

推荐 precedence 对应 `R3_DECISION_02=OPTION_A`，尚未批准。禁止使用名称相似、跨站模糊匹配、第一条记录胜出或未经证据的自动 replacement。

## 4. 关联与生命周期

逻辑关系为：

```text
device_inventory(AC)
  -> AcDomain
  -> AcApObservation(source AC + source Site + revision)
  -> ApResource
  -> PhysicalApIdentity evidence
```

关系 projection 使用 `Current + PreviousEffective + Recent10` 语义；只有有效变化进入 Recent10，未变化刷新不生成新变化记录。采集失败不得删除上一条有效 Current，也不得把失败改写为 AP offline。

## 5. 约束与禁止事项

1. `ap_resource_id`、`observation_id`、`physical_ap_identity_id` 均为 opaque ID，UI 不自行计算。
2. 任何 AP 资源响应先应用 source AC/Site authorization，再做摘要或聚合。
3. 未授权 observation 不参与 serial/MAC 聚合、state 聚合或 group membership。
4. identity conflict 必须可见，并携带 reason code、evidence refs 和 revision。
5. B0 不改变数据库、不添加 model、不补 migration；本文件只冻结待批准后的实现输入。

## 6. 证据来源

- `src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-204`：当前 parser 的 AP serial、名称、组和 MAC 字段。
- `src/NetConsole.Next.Service/DeviceManagement.cs:94-157,246-278`：设备库存读写边界。
- `D:\study\NetConsole-Next-Workspace\architecture-replanning\R0-R1-20261101\04_DEVICE_AND_AC_DOMAIN_MODEL.md:9-35`：规划中的 Device/AcDomain/ApResource/Observation 分层。
- `docs/ui-r3-wireless-business/R3_B0_BUSINESS_DECISION_PACKAGE.md`：`R3_DECISION_01`、`R3_DECISION_02`。
