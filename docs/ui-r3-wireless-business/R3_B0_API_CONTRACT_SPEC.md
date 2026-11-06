# R3-B0 无线业务 API 合同规格（修订版）

## 2026-11-06 当前切片边界

AP Group 来源身份修复为 `LOCAL_ACCEPTANCE_PASS`。下方“本轮未实现/0 gap”等计数属于冻结设计批次，不是当前产品状态；本次不据此重开授权，也不宣称所有 API 或 gap 已关闭。

API-W07/W08 继续为只读入口，按 [当前 AP Group 合同](R3_B0_AP_GROUP_CONTRACT.md) 返回 typed source evidence 与身份状态。实际 JSON 使用 camelCase：名称 only 为 `groupScope=AC`、`groupIdentityStatus=CANDIDATE`、`sourceGroupKey=null`、`groupIdentityKey=null`；可通过受 source Site claims 保护的 opaque ID 读取候选详情。不能从 display name 生成稳定 key。

DOMAIN 要同时具备显式 domain-shared evidence、确认的 Domain/相符 ID 与一致的成员集合；否则保持 AC/candidate/conflict。`membershipRevision` 独立于 `sourceRevision`，不因改名或无变化刷新改变。响应附 `sourceObservationIds`、`sourceAcDeviceIds`、`sourceEvidenceRefs`、`observedAtMs`、`conflictReason` 和 `sourceGroupObservations`；成员/详情在 Site/source AC 过滤后聚合，只带该组实际关联的 observations；sourceAcId 过滤保留当前授权投影的 canonical opaque resource ID，MAC-only 来源返回的成员链接仍可由全局 resource detail 解析。

schema24 只持久化可重建 group projection；schema23 已归 Linux 切片。完整身份与 Domain/group 生命周期、写回和其他未验证 API 不由本切片宣告完成。`py -3.13 -B tests/r3_dual_ac_api_e2e.py` 已通过全部 16 个 marker，使用真实 HTTP 服务与离线 fixture，包含 sourceAcId/MAC-only canonical 成员链接。schema24 本机迁移与 Service harness 通过；Chrome 15 状态为 API mocked 浏览器证据，非 live 设备。当前切片为 `LOCAL_ACCEPTANCE_PASS`，不等同完整 R3/API 验收。

## 历史合同与早期实现记录


修订日期：2026-11-04
状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。本文件区分“已有可复用 API”“已有但缺无线语义”和“未来新 API”；不表示 endpoint 已实现。本轮 `API_GAPS_IMPLEMENTED_THIS_ROUND=0`。

冻结更新：7 项业务决策已冻结，10 个 gap 仍为 `DESIGNED_NOT_IMPLEMENTED`；API-W09 只内部复用 P2B5 predicate，不新增 route。

## 1. 通用响应与授权合同

无线新 API 建议统一返回：

```json
{
  "items": [],
  "page": {"limit": 50, "nextCursor": null},
  "asOf": 0,
  "scope": {"status": "FULL", "claimsRevision": "..."},
  "sourceRevision": "...",
  "warnings": []
}
```

单资源必须带 `id`、`revision`、`sourceRefs`、`scopeStatus`；冲突必须带 `reasonCode`，不能用 HTTP 200 的空数组伪装为不存在。

R3-B1 从 API-W01/W02/W03 的第一条无线 route 开始复用 P2B5 Site claims，并在服务端执行 AC scope、observation filter、serial/MAC no-bypass、opaque ID no-bypass、fail-closed 和 no unauthorized count。list 不返回未经授权对象的 global total；detail 对不可见对象返回不可区分的 `404 RESOURCE_NOT_VISIBLE`。客户端不能提交 claims 作为权限来源。

## 2. AC inventory 与 AP facts

| API contract | 当前事实 | B0 定义 | 状态 |
| --- | --- | --- | --- |
| AC inventory | `GET /api/system/devices?deviceType=AC`、`GET /api/system/devices/{id}` 已存在并走 Site scope | 复用 device ID；不新增 AC inventory | `IMPLEMENTED_PARTIAL` |
| AC profile/facts | `GET /api/system/h3c/profiles`、`GET /api/system/h3c/profiles/{id}/facts` 已存在；facts 仍是 generic fact | 未来增加 wireless typed projection，不把 generic fact 当 AP resource | `IMPLEMENTED_PARTIAL` |
| offline replay | `POST /api/system/h3c/profiles/{id}/offline-replay` 已有 bounded fixture | 仅作为测试/证据入口，不能替代 typed AP API | `IMPLEMENTED_PARTIAL` |

来源：`src/NetConsole.Next.Service/Program.cs:307-315,360-363,877-928`、`src/NetConsole.Next.Service/H3cFacts.cs:8-30,103-123`。

## 3. API-W01～W12 合同与状态

| ID | Method/endpoint 或内部合同 | 主要响应/职责 | 授权要求 | 状态 | Gap |
| --- | --- | --- | --- | --- | --- |
| API-W01 | GET `/api/system/wireless/ac-domains` | AC domain + members + topology | R3-B1 P2B5 AC scope | `DESIGNED_NOT_IMPLEMENTED` | G01 |
| API-W02 | GET `/api/system/wireless/ap-resources` | typed AP resource + visible identity | visible observation predicate | `DESIGNED_NOT_IMPLEMENTED` | G02/G04 |
| API-W03 | GET `/api/system/wireless/ap-resources/{id}` | identity evidence + visible observations | opaque ID + source predicate | `DESIGNED_NOT_IMPLEMENTED` | G02/G03/G04 |
| API-W04 | GET `/api/system/wireless/ac-domains/{id}/ap-associations` | relation + source/revision | source AC/domain Site claim | `IMPLEMENTED_READ_ONLY` | G03 |
| API-W05 | GET `/api/system/wireless/ap-resources/{id}/runtime-state` | raw/normalized/freshness/conflict | visible observations only | `DESIGNED_NOT_IMPLEMENTED` | G05 |
| API-W06 | GET `/api/system/wireless/ac-domains/{id}/topology` | members, HA/IRF evidence | domain member scope | `DESIGNED_NOT_IMPLEMENTED` | G06 |
| API-W07 | GET `/api/system/wireless/ac-domains/{id}/ap-groups` | AC/domain-scoped groups | group source Site claim | `IMPLEMENTED_READ_ONLY` | G07 |
| API-W08 | GET `/api/system/wireless/ap-groups/{id}` and `/members` | group detail/provenance and visible AP members | group + AP predicate | `IMPLEMENTED_READ_ONLY` | G07 |
| API-W09 | INTERNAL `P2B5_SCOPE_PREDICATE`; 不新增 route | 当前身份授权摘要只供服务端 predicate 使用 | P2B5 authority；client claims forbidden | `REUSE_EXISTING_AUTHORITY_NO_NEW_ROUTE` | G08 |
| API-W10 | GET `/api/system/wireless/tasks/{id}` | scope snapshot/current authorization | current claims at read | `DESIGNED_NOT_IMPLEMENTED` | G09 |
| API-W11 | POST `/api/system/wireless/tasks` | plan/task accepted, no implicit write | creation scope + current claims | `DESIGNED_NOT_IMPLEMENTED` | G09 |
| API-W12 | GET `/api/system/wireless/audit` | bounded event + manifest/hash refs | current Site subject predicate | `DESIGNED_NOT_IMPLEMENTED` | G10 |

API-W09 不再拟议 `GET /api/system/wireless/site-claims`。P2B5 已是服务端 authority；本轮只修订 API contract，不新增 claims 暴露接口，也不把 API-W09 误报为产品 route。

## 4. AP Group API 细节

AP group 的 `group_scope` 只能由 source evidence 确认：

```text
DOMAIN: GROUP_IDENTITY_KEY=DOMAIN|ac_domain_id|source_group_key
AC:     GROUP_IDENTITY_KEY=AC|source_ac_device_id|source_group_key
```

Domain-owned 需要 domain-shared evidence；否则默认 AC-owned 或 `UNKNOWN`。`display_name` 不是唯一键；source key、source AC/domain ID、source revision 和 membership revision 必须返回。未授权成员不能影响 group count/state/conflict。

## 5. Task/Report API 与撤销语义

API-W10/W11 的 task scope 必须至少包含：

```text
task_id
actor_id
scope_snapshot
claims_revision_at_creation
authorized_subject_ids
source_ac_device_ids
source_site_ids
created_at
current_scope_status
```

`scope_snapshot` 只限制目标上限，不是永久授权凭证。实际执行目标为 `CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS`；在 connect、每个 command/item、retry、resume、report publish 前重新检查 claims。撤销后的未开始 item 标记 `REVOKED`；已进入不可安全中断的单个 bounded operation 只能完成当前原子边界或安全中止，封存 partial result，标记 `REVOKED_AFTER_START`，不启动后续操作。

完成后再次撤销 claims 时，Task Detail/List/Result/Report/Download/Generic Task/Audit 均不能继续读取被撤销 Site 的结果。部分撤销只返回仍授权的结果并标 `PARTIAL`，不泄露撤销 Site 的记录、状态、数量、来源或 hash。新增 Site claims 不扩大旧任务目标。

现有 generic routes `GET /api/system/tasks`、`POST /api/system/h3c/task-definitions`、`POST /api/system/tasks/{definitionId}/runs`（`Program.cs:659,953-989`）仅是实现边界证据，不能直接满足 wireless task contract；generic route 必须增加 subject predicate 或排除 wireless result。

## 6. 分页、revision 与错误

- 所有 list 默认 limit 50，上限 100；cursor 绑定 filter、claims revision、source revision 和 `asOf`。
- 读 API 支持 `asOf`，不把新鲜事实冒充历史快照。
- unauthorized list 只返回授权子集，不返回未经授权对象的 total/count/名称。
- guessed AP ID 或不可见 detail 返回 `404 RESOURCE_NOT_VISIBLE`；claims authority 不可用返回 `503 WIRELESS_AUTHORITY_UNAVAILABLE`；请求 Site 越权使用现有 `P2B_SITE_SCOPE_DENIED` 语义。
- revision mismatch 返回 409；身份/关系冲突返回结构化 `reasonCode`。
- 任何响应不得包含 credential reference 的可解析秘密、raw command output、未授权 Site 名称或隐藏对象 count。

## 7. 写入和实现边界

无线第一阶段只允许 read/observation projection 和 bounded offline fixture。真实 AC/AP command、config write、AP group move、scan 和 task execution 均不在 B0；未来 task API 必须继承 snapshot/current claims 双状态。API-W01..W08、W10..W12 全部是设计，不是现有路由；API-W09 是内部复用合同，不是新增 route。
