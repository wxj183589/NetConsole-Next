# R3-B0 AP Group 合同

状态：`PROPOSED_PENDING_APPROVAL`。AP group 在 B0 只定义只读业务合同；`AP_GROUP_WRITE=OUT_OF_SCOPE`。

## 1. 所有权与稳定 key

推荐 `R3_DECISION_04=OPTION_A`：group 是 AC/domain 侧观察到的资源，不是平台全局可写对象。

```text
ap_group_id: opaque projection id
source_ac_domain_id
source_ac_device_id (nullable for domain-level group)
source_group_key: normalized vendor group key
display_name: observed name
site_claim_projection
member_ap_resource_ids: visible members only
membership_revision
observed_at_ms
visibility: FULL | PARTIAL | NONE
```

稳定语义是 `source_ac_domain_id + source_group_key`；名称不是全局唯一。若相同名称出现在不同 AC/domain，必须形成不同 group resource。若同一 AP 在不同来源属于不同组，保留多个 membership observation，不覆盖。

## 2. 建议只读 API

以下是未来 endpoint 合同，不代表本轮已经存在：

| Method | Endpoint | 目的 | 权限 |
| --- | --- | --- | --- |
| GET | `/api/system/wireless/ac-domains/{domainId}/ap-groups` | 分页列出可见组 | source domain Site claim |
| GET | `/api/system/wireless/ap-groups/{groupId}` | 组详情和 provenance | group source Site claim |
| GET | `/api/system/wireless/ap-groups/{groupId}/members` | 可见 AP 成员 | group + AP observation predicate |

响应必须返回 `source_ac_domain_id`、`source_group_key`、`membership_revision`、`scope_status` 和 `as_of`。不能只返回 name/member count 造成无法审计的聚合。

## 3. 读写和安全边界

- B0/B1 不增加 group create/update/delete、AP move、AC write-back 或 command endpoint。
- group 结果继承 source AC/domain 的 Site claims；同名 group 不得跨 Site join。
- AP 成员只从当前授权 observation 投影；未授权成员不能影响 count、state 或 conflict。
- claims 撤销后，详情、成员和导出均 fail closed。
- 空组、组变化、source group key 冲突均为可见状态，不自动删除历史 evidence。

## 4. 测试入口

至少覆盖：同名跨 AC、同名跨 Site、AP 跨 AC 组变化、空组、组成员撤销、group source conflict、SUPERADMIN 全局读取和无授权 403/404 语义。

## 5. 依据

- `src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-197`：当前只有 parser `Group` 文本。
- `src/NetConsole.Next.Service/Program.cs:596-610`：现有平台 device group route 不能直接复用为无线 AC group。
- `docs/ui-r3-wireless-business/R3_B0_BUSINESS_DECISION_PACKAGE.md`：`R3_DECISION_04`。
