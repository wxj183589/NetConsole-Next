# R3-B0 无线业务 API 合同规格

状态：`PROPOSED_PENDING_APPROVAL`。本文件区分“已有可复用 API”“已有但缺无线语义”和“未来新 API”；不表示 endpoint 已实现。

## 1. 通用响应合同

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

## 2. AC inventory 与 AP facts

| API contract | 当前事实 | B0 定义 |
| --- | --- | --- |
| AC inventory | `GET /api/system/devices?deviceType=AC`、`GET /api/system/devices/{id}` 已存在并走 Site scope | 复用 device ID；不新增 AC inventory |
| AC profile/facts | `GET /api/system/h3c/profiles`、`GET /api/system/h3c/profiles/{id}/facts` 已存在；facts 仍是 generic fact | 未来增加 wireless typed projection，不把 generic fact 当 AP resource |
| offline replay | `POST /api/system/h3c/profiles/{id}/offline-replay` 已有 bounded fixture | 仅作为测试/证据入口，不能替代 typed AP API |

来源：`src/NetConsole.Next.Service/Program.cs:307-315,360-363,877-928`、`src/NetConsole.Next.Service/H3cFacts.cs:8-30,103-123`。

## 3. 未来 wireless read APIs

以下 endpoint 需在用户批准 7 项决策、实现对应 service 后才可进入代码：

| ID | Method/endpoint | 主要响应 | 解决 gap |
| --- | --- | --- | --- |
| API-W01 | GET `/api/system/wireless/ac-domains` | AC domain + members + topology | G01 |
| API-W02 | GET `/api/system/wireless/ap-resources` | typed AP resource + visible identity | G02/G04 |
| API-W03 | GET `/api/system/wireless/ap-resources/{id}` | identity evidence + visible observations | G02/G03/G04 |
| API-W04 | GET `/api/system/wireless/ac-domains/{id}/ap-associations` | relation + source/revision | G03 |
| API-W05 | GET `/api/system/wireless/ap-resources/{id}/runtime-state` | raw/normalized/freshness/conflict | G05 |
| API-W06 | GET `/api/system/wireless/ac-domains/{id}/topology` | members, HA/IRF evidence | G06 |
| API-W07 | GET `/api/system/wireless/ac-domains/{id}/ap-groups` | AC-scoped groups | G07 |
| API-W08 | GET `/api/system/wireless/ap-groups/{id}/members` | visible group members | G07 |
| API-W09 | GET `/api/system/wireless/site-claims` | subject claims/snapshot status | G08 |
| API-W10 | GET `/api/system/wireless/tasks/{id}` | scope snapshot/current authorization | G09 |
| API-W11 | POST `/api/system/wireless/tasks` | plan/task accepted, no implicit device write | G09 |
| API-W12 | GET `/api/system/wireless/audit` | bounded event + manifest/hash refs | G10 |

## 4. Write/task boundary

无线第一阶段只允许 read/observation projection 和 bounded offline fixture。真实 AC/AP command、config write、AP group move、scan 和 task execution 均不在 B0；未来 task API 必须继承 `R3_DECISION_06` 的 snapshot/current claims 双状态。

现有 generic task routes：`GET /api/system/tasks`、`POST /api/system/h3c/task-definitions`、`POST /api/system/tasks/{definitionId}/runs`（`Program.cs:659,953-989`）仅作为实现边界证据，不能直接宣称满足 wireless task contract。

## 5. 资源字段最低集合

### AP resource

`id`, `displayName`, `identityStatus`, `serialEvidence`, `normalizedMacSet`, `sourceObservationIds`, `sourceAcDomainIds`, `scopeStatus`, `firstSeenMs`, `lastSeenMs`, `revision`。

### Association

`id`, `apResourceId`, `sourceAcDeviceId`, `sourceAcDomainId`, `sourceSiteId`, `relationStatus`, `observedAtMs`, `relationRevision`, `sourceManifestRef`。

### Runtime state

`rawState`, `normalizedState`, `freshness`, `ageMs`, `confidence`, `sourceObservationIds`, `stateReasonCode`, `conflict`。

### Task/audit

Task 要有 `scopeSnapshot`, `claimsRevisionAtCreation`, `currentScopeStatus`, `subjectRefs`, `state`, `outcomeRef`；audit 只要 bounded action/actor/subject/revision/reason/manifest hash refs。

## 6. 分页、revision 与错误

- 所有 list 默认 limit 50，上限 100；cursor 必须绑定 filter、scope revision 和 `asOf`。
- 读 API 支持 `asOf`，不把新鲜事实冒充历史快照。
- revision mismatch 返回 409；权限失败使用现有 `P2B_SITE_SCOPE_DENIED` 语义；身份/关系冲突返回结构化 `reasonCode`。
- 任何响应不得包含 credential reference 的可解析秘密、raw command output、未授权 Site 名称或隐藏对象 count。

## 7. 实现前置

API-W01..W12 全部是设计，不是现有路由；实现顺序由 `R3_B0_BATCH_IMPLEMENTATION_PLAN.md` 约束。7 项决策仍待用户批准，故 `API_GAPS_REMAINING=10`。
