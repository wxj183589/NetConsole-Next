# R3-B0 AP Group 合同（修订版）

修订日期：2026-11-04
状态：`PROPOSED_PENDING_APPROVAL`。AP group 在 B0 只定义只读业务合同；`AP_GROUP_WRITE=OUT_OF_SCOPE`。

## 1. 所有权与两层作用域

推荐 `R3_DECISION_04=OPTION_A`：group 是 AC/domain 侧观察到的资源，不是平台全局可写对象。必须区分两种作用域：

### Domain-owned group

只有 observation 明确证明 AP group 属于整个 AC Domain（例如厂商输出明确 domain-shared/group scope，或多个成员 AC 提供一致且带 domain-level source evidence 的 group key）时，才允许：

```text
GROUP_SCOPE=DOMAIN
GROUP_IDENTITY_KEY=DOMAIN|ac_domain_id|source_group_key
```

`source_ac_domain_id` 是稳定 domain 作用域；成员 AC 仍作为 provenance 保留。相同 domain ID 本身不是 domain-owned 证据。

### AC-owned group

如果 group 明确归属于具体 AC，或尚未证明它是 domain-shared resource，则必须使用：

```text
GROUP_SCOPE=AC
GROUP_IDENTITY_KEY=AC|source_ac_device_id|source_group_key
```

这条规则优先于“同一个 domain 下看起来相同”的 UI 归并。没有 `source_group_key` 或 source AC ID 时，observation 保留为 `CANDIDATE/UNKNOWN`，不得产生可合并的稳定 group identity。

## 2. 稳定 ID 与字段

`ap_group_id` 是服务端生成的 opaque projection ID；其唯一性和重建依据是以下不可变 identity key，而不是 display name：

```text
ap_group_id: opaque stable projection id
group_identity_key: DOMAIN|domain_id|source_group_key
                     or AC|ac_device_id|source_group_key
source_ac_device_id: required for AC scope; provenance for DOMAIN scope
source_ac_domain_id: required when domain is known
source_group_key: normalized vendor/source key, not display name
group_scope: DOMAIN | AC | UNKNOWN
group_identity_status: CONFIRMED | CANDIDATE | CONFLICT | UNKNOWN | ENDED
display_name: observed name, never identity key
source_revision: source/group relation revision
source_observation_ids: visible source observations only
member_ap_resource_ids: visible members only
membership_revision
observed_at_ms
visibility: FULL | PARTIAL | NONE
```

`source_group_key` 相同且 identity scope 相同，才可形成同一 source group projection；仅 display name 相同不能合并。不同 display name 只有在 vendor/source key 明确相同且 revision 可追踪时，才可作为同一组的名称变化；否则保留独立 observation/conflict。

## 3. 冲突与生命周期规则

必须覆盖并保持独立 evidence：

1. 不同 AC 存在同名 AP 组：形成不同 `AC|device|key` group。
2. 同一 Domain 下不同成员 AC 存在同名组：仍按 AC scope 分开，除非有明确 domain-shared evidence。
3. 相同组名对应不同 source group key：不合并，返回 `CONFLICT`/`CANDIDATE`。
4. 不同组名对应同一稳定 source group key：在同一 scope 内记录 display-name change 和 revision，不创建全局 alias。
5. AP 在两个 AC 下属于不同组：保留两个 membership observations，不覆盖。
6. AP 组名称发生变化：保持 group identity key，写入名称变化 evidence；没有 stable source key 时不自动迁移 identity。
7. AP 组来源缺失：group observation 可展示为 `UNKNOWN`，不能产生可授权的稳定 group ID。
8. Domain 成员发生变化：不自动把已有 AC group 升级为 Domain group；以新 evidence/revision 重新确认，旧关系标记 ended 或 conflict。

禁止仅根据组名合并、仅根据 Domain ID 合并未证实的 AC 局部组，或将 UI 导航结构当作 AC 配置事实。

## 4. 建议只读 API

以下是未来 endpoint 合同，不代表本轮已经存在：

| Method | Endpoint | 目的 | 权限 | 状态 |
| --- | --- | --- | --- | --- |
| GET | `/api/system/wireless/ac-domains/{domainId}/ap-groups` | 分页列出可见 Domain/AC groups | source AC/domain Site claim | `DESIGNED_NOT_IMPLEMENTED` |
| GET | `/api/system/wireless/ap-groups/{groupId}` | 组详情和 provenance | group source Site claim | `DESIGNED_NOT_IMPLEMENTED` |
| GET | `/api/system/wireless/ap-groups/{groupId}/members` | 可见 AP 成员 | group + AP observation predicate | `DESIGNED_NOT_IMPLEMENTED` |

响应必须返回 `group_scope`、`group_identity_key`、`source_ac_device_id`、`source_ac_domain_id`、`source_group_key`、`source_revision`、`membership_revision`、`scope_status` 和 `as_of`。不能只返回 name/member count 造成无法审计的聚合。

## 5. 读写和安全边界

- B0/B1 不增加 group create/update/delete、AP move、AC write-back 或 command endpoint。
- group 结果继承 source AC/domain 的 Site claims；同名 group 不得跨 Site join。
- AP 成员只从当前授权 observation 投影；未授权成员不能影响 count、state 或 conflict。
- claims 撤销后，详情、成员和导出均 fail closed。
- `GROUP_SCOPE=DOMAIN` 必须由 source evidence 证明；否则降级为 AC scope 或 unknown，不得由客户端传入 scope。
- 分页 cursor 绑定当前 claims revision、filter 和 as-of；不返回未经授权对象的 total/count。

## 6. 测试入口

覆盖 SC-28/SC-29 和本轮新增 SC-36..SC-41：同名跨 AC、同名跨 Domain 成员、明确 domain-owned、缺失 source ID、source key/name 变化、Domain 成员变化、AP 跨 AC 组变化、空组、跨 Site denial 和 SUPERADMIN provenance。

## 7. 依据

- `src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-197`：当前只有 parser `Group` 文本。
- `src/NetConsole.Next.Service/Program.cs:596-610`：现有平台 device group route 不能直接复用为无线 AC group。
- `D:\study\NetConsole-Next-Workspace\architecture-replanning\R0-R1-20261101\04_DEVICE_AND_AC_DOMAIN_MODEL.md:9-35`：AC domain/observation 分层原则。
- `docs/ui-r3-wireless-business/R3_B0_BUSINESS_DECISION_PACKAGE.md`：`R3_DECISION_04`。
