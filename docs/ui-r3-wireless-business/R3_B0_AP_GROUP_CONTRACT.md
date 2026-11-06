# R3-B0 AP Group 合同（修订版）

## 2026-11-06 当前切片：来源身份合同

状态：`LOCAL_ACCEPTANCE_PASS`。本节记录已完成本机验收的 AP Group 只读来源身份修复；下方冻结设计与旧实现记录保留为历史。本节不表示整套无线业务、57 场景或真实设备验收完成。

- 统一采用 typed `WirelessSourceGroupEvidence`：来源 AC、Site、显式 `sourceGroupKey`、display name、实际 observation IDs、captured time、source revision、Raw manifest 和 parser revision。来源 AC/Site 或 observation 归属无法验证时，不将该 evidence 纳入可见投影。AP listing 中的 `Group` 文本只证明临时组关系，不提供厂商稳定 key。
- 名称 only 的可见组为 `groupScope=AC`、`groupIdentityStatus=CANDIDATE`、`sourceGroupKey=null`、`groupIdentityKey=null`。允许通过 opaque projection ID 在当前 source Site claims 内读取候选详情；该 ID 不成为稳定身份或授权来源，也不使用名称伪造 key。候选 ID 可随来源 evidence 变化。
- 有显式厂商/source key 及完整 provenance，且未证明 domain-shared 时，采用 `AC|source_ac_device_id|source_group_key`。只有明确 `domainSharedEvidenceRefs`、已确认的 Domain、相同 source Domain ID，且 evidence 中 Domain 成员集合与当前确认集合一致，才采用 `DOMAIN|ac_domain_id|source_group_key`。同名、相同 Domain ID 或两个 AC 返回相同 key 均不足以确认 DOMAIN。
- `membershipRevision` 只表达 scoped identity 与当前成员关系：来源 AC/resource 集合、实际来源 AC 集合，以及 DOMAIN 组的确认成员集合。仅改名、captured time/source revision 更新或无变化刷新均不得改变它；这些 evidence 变化由独立 `sourceRevision` 表达。较旧 observation 作为 provenance 保留，不并入当前 membership。
- 相同 scope/name 对应不同 source key 时保持独立并显示 conflict；同一 key 的并发互斥名称保留冲突 evidence。Domain 成员变化后，旧 shared evidence 不匹配时不得升级为 DOMAIN，应按可见证据保留 AC 范围与 candidate/conflict 原因。
- Site claims 过滤先于身份投影；sourceAcId 过滤后重建当前组的 members、count、state、revision 与 evidence。已授权投影中的 opaque AP resource locator 保持全局可解析，包含仅有 MAC evidence 的来源；这不带入其他来源的 summary 或 observations。不可见 Domain 成员/shared evidence 不得泄露或影响可见 revision。成员详情只返回该组实际 observation 关联。

实际 JSON 使用 camelCase；下文 snake_case 是概念合同。当前响应包括 `groupScope`、可空 `groupIdentityKey` / `sourceGroupKey`、`groupIdentityStatus`、`membershipRevision`、`sourceRevision`、`sourceObservationIds`、`sourceAcDeviceIds`、`sourceEvidenceRefs`、`observedAtMs`、`conflictReason` 和 typed `sourceGroupObservations`，并保留 source AC/Domain、scope 与 as-of 信息。

schema23 已用于 Linux 连接切片；本切片使用 schema24，只调整可重建 `wireless_ap_groups` projection，保存上述身份/provenance，不提升为 Raw/Facts 的事实 Authority。历史名称键降为 `AC / CANDIDATE / null key`，不据此确认身份。完整 AP candidate/replacement、Domain/group 创建结束与变更历史生命周期仍后续；`AP_GROUP_WRITE=OUT_OF_SCOPE`。

集成核验入口：`tests/NetConsole.Next.Service.Tests/WirelessGroupTests.cs`、`tests/r3_dual_ac_api_e2e.py`、`web/src/views/WirelessView.test.ts`、`tests/wireless_browser_e2e.mjs`。本机 AP Group 10 个合同场景、Wireless projection、schema24 事务回滚/幂等/fresh/reopen/旧数据降级、真实 HTTP 离线 fixture 的 16 个 marker 与完整 Service harness 均通过；Chrome API mocked 浏览器 15 状态及前端 9 files/29 tests、typecheck、独立 Vite 通过。结果为 `LOCAL_ACCEPTANCE_PASS`，具体证据范围见 [本机验收记录](R3_FULL_CHAIN_FINAL_ACCEPTANCE.md)。未取得真实设备证据的兼容性仍为 `REAL_DEVICE_PENDING`。

## 历史冻结设计与早期实现记录


修订日期：2026-11-04
状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。AP group 在 B0 只定义只读业务合同；`AP_GROUP_WRITE=OUT_OF_SCOPE`。

冻结更新：D04 为修订后的 `OPTION_A`。只有明确 domain-shared evidence 才可使用 Domain-owned key；否则必须使用 AC-owned key；同名 group 不合并，平台不写入。

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

以下只读 endpoint 已接入当前无线读模型；它们仍不提供 AP Group 写回能力：

| Method | Endpoint | 目的 | 权限 | 状态 |
| --- | --- | --- | --- | --- |
| GET | `/api/system/wireless/ac-domains/{domainId}/ap-groups` | 分页列出可见 Domain/AC groups | source AC/domain Site claim | `IMPLEMENTED_READ_ONLY` |
| GET | `/api/system/wireless/ap-groups/{groupId}` | 组详情和 provenance | group source Site claim | `IMPLEMENTED_READ_ONLY` |
| GET | `/api/system/wireless/ap-groups/{groupId}/members` | 可见 AP 成员 | group + AP observation predicate | `IMPLEMENTED_READ_ONLY` |

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

当前实现证据：`src/NetConsole.Next.Service/Program.cs` 的三条只读 AP Group route、`tests/r3_dual_ac_api_e2e.py` 的 group scope/detail/provenance 断言，以及 `tests/wireless_browser_e2e.mjs` 的组详情点击验收。
