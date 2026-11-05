# R3 无线业务 API 合同审计

日期：2026-11-04

范围：只读审计 NetConsole-Next 当前源码、架构冻结材料、SmartHub clean-room 参考交接和已有离线回放测试。不连接真实 AC/AP，不执行真实任务，不修改 R3 产品代码、数据库 schema 或 migration。

## 结论摘要

当前可以复用的真实能力是：普通设备库存中的 `AC` 类型、Site/group/稳定 `device_inventory.id`、H3C profile/凭据引用、allowlist 只读命令、离线 H3C parser/replay、`h3c_facts` Current/history 查询、bounded scheduler、Raw/task recovery、既有 P2B5 Site claims 以及 LLDP parser。当前没有无线业务专用的 AP resource、AC domain、AC↔AP association、AP group、规范化 R/M/R/B/I 状态、multi-AC topology 或无线业务查询 API。

因此：

```text
R3_API_CONTRACT_READINESS=PARTIAL
R3_DATA_MODEL_READINESS=PARTIAL
R3_SITE_AUTHORIZATION_READINESS=PARTIAL
R3_WIRELESS_UI_IMPLEMENTATION=NOT_STARTED
```

## 能力矩阵

| ID | 能力 | 现有实现 | 实现状态 | 合同状态 |
| --- | --- | --- | --- | --- |
| R3-C01 | AC Inventory | `device_inventory.device_type=AC`、Site/group/CRUD；没有 AC domain | `IMPLEMENTED_PARTIAL` | 普通库存可复用，AC 业务域缺失 |
| R3-C02 | AP Facts | H3C parser 有 AP/地址/radio records；replay 写入通用 `h3c_facts`/opaque command facts | `IMPLEMENTED_PARTIAL` | 事实来源存在，typed AP API 缺失 |
| R3-C03 | AC↔AP | 没有 association/resource 表或 API | `DESIGNED_NOT_IMPLEMENTED` | 不足以支持跨 AC 去重/授权 |
| R3-C04 | AP Group | parser 的 AP 行有 `Group` 字段；无 group resource/API | `IMPLEMENTED_PARTIAL` | 只能作为原始证据，不能作为业务合同 |
| R3-C05 | Serial/MAC identity | parser 可读 serial/MAC；无 AP 主身份、规范化冲突和替换合同 | `EVIDENCE_REQUIRED` | 不能用名称或 MAC 尾号补齐 |
| R3-C06 | R/M、R/B、I | parser 有原始 `State`；无规范化枚举、可信度、过期和冲突投影 | `DESIGNED_NOT_IMPLEMENTED` | 状态合同未冻结 |
| R3-C07 | Stack/Cluster/1+1 | 架构文档建议 `AcDomain.domain_kind`；源码无实现 | `DESIGNED_NOT_IMPLEMENTED` | 多 AC 拓扑未实现 |
| R3-C08 | Site Scope | R2 inventory/facts route 已有 claims；H3C profile list 和 task definition/run 不是无线 scope contract | `IMPLEMENTED_PARTIAL` | 无线授权适配器缺失 |
| R3-C09 | Task API | 单设备/单命令 definition/run、bounded scheduler、Raw manifest | `IMPLEMENTED_PARTIAL` | 无 business plan/item/partial outcome contract |
| R3-C10 | Audit API | device/batch/task/host-key 等已有 audit；无无线关联/冲突/刷新事件 | `IMPLEMENTED_PARTIAL` | 无线审计字段缺失 |
| R3-C11 | LLDP | H3C LLDP parser/fact 和 allowlist；无 AP identity projection | `IMPLEMENTED_PARTIAL` | 可做证据输入，不是 AP 关系 API |
| R3-C12 | History | `h3c_facts.is_current` 与 `history=true`；无 AP Current/PreviousEffective/Recent10 泛化投影 | `IMPLEMENTED_PARTIAL` | 无线动态历史合同缺失 |

## 逐项代码/文档核对

### R3-C01 AC Inventory

- `SOURCE_FILE`：`docs/DEVICE_DATA_MODEL.md:5-25`；`src/NetConsole.Next.Service/DeviceManagement.cs:122-157,246-278`。
- `EXISTING_API`：设备 inventory/list/detail/group/CRUD API；Web 设备类型选项包含 `AC`，见 `web/src/views/DevicesView.vue:32`。
- `DATA_MODEL`：`device_inventory.id` 是稳定设备 ID，`site_id`、`group_id`、`vendor`、`model`、`management_ip` 和可选 `terminal_profile_id`；没有 `ac_domains`/`ac_members`。
- `AUTHORIZATION_MODEL`：R2 设备查询/写入通过 `DeviceSiteScope`/P2B5 claims；设备库存层可作为 AC 导航入口。
- `TEST_EVIDENCE`：`tests/r2_site_claims_e2e.py` 实测 31/31；本轮 Browser CRUD PASS。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：可复用普通 AC inventory；无法表达一个逻辑管理域包含多个物理 AC 成员。
- `MISSING_FIELDS`：`ac_domain_id` 或独立 domain 资源、domain kind、member role/priority/state/source revision。
- `RISKS`：为无线页面重新生成 AC 身份会与设备库存分叉；把 hostname/IP 当 AC identity 会漂移。
- `NEXT_ACTION`：R3-B1 先定义 inventory-to-domain adapter，保留原 `device_inventory.id`。

### R3-C02 AP Facts

- `SOURCE_FILE`：`src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-220`；`src/NetConsole.Next.Service/H3cFacts.cs:8-125,128-199`；`src/NetConsole.Next.Service/H3cComware.cs:326-408`。
- `EXISTING_API`：`GET /api/system/h3c/profiles/{id}/facts?kind=&history=&limit=`；`POST /api/system/h3c/profiles/{id}/offline-replay`。
- `DATA_MODEL`：`H3cApFact(Name, Id, State, Model, Serial, Group, OnlineTime)`、地址/radio record 可被 parser 产生；落库是 profile/kind/identity/status/source/captured/is_current/payload_json。
- `AUTHORIZATION_MODEL`：facts GET 做 P2B5 scope 和 profile-in-scope 校验；offline replay 为 SUPERADMIN；这是 H3C profile/fact 权限，不是 AP 业务授权。
- `TEST_EVIDENCE`：Service harness 的 H3C parser/replay 与 36 fixture 回归 PASS，来源明确为 offline fixture；无实时 AC/AP 证据。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：离线事实来源真实存在，但 typed AP resource/API/UI 未实现。
- `MISSING_FIELDS`：AP resource ID、observation ID/run、source AC/domain、raw hash、verification level、freshness/uncertainty、typed API envelope。
- `RISKS`：把 opaque `command.display.wlan.ap.all` 或离线 fixture 直接作为无线业务 Current 会掩盖未解析、旧数据和冲突。
- `NEXT_ACTION`：R3-B0 冻结 typed fact envelope；R3-B2 再做 AP resource/association。

### R3-C03 AC↔AP Relation

- `SOURCE_FILE`：当前源码没有 association 表/API；设计参考为 `architecture-replanning/R0-R1-20261101/04_DEVICE_AND_AC_DOMAIN_MODEL.md:10-39`。
- `EXISTING_API`：没有 AC/AP association endpoint；profile facts API 只能按 H3C profile 读取 facts。
- `DATA_MODEL`：只有 parser AP name/id/serial/group 等字段和 `h3c_facts.profile_id`，没有跨 AC relation。
- `AUTHORIZATION_MODEL`：不能从现有 generic facts API 推导跨 AC AP 可见性。
- `TEST_EVIDENCE`：没有跨 AC AP 关系测试；现有离线 replay 不证明一对多关系。
- `IMPLEMENTATION_STATUS`：`DESIGNED_NOT_IMPLEMENTED`。
- `CONTRACT_STATUS`：未准备好。
- `MISSING_FIELDS`：`ap_resource_id`、`ac_domain_id`、`source_device_id`、relation state、first/last observed、observation/revision、conflict code。
- `RISKS`：同一 AP 被两个 AC 发现时重复建资产或错误合并；迁移 AC 后历史断裂。
- `NEXT_ACTION`：用户批准身份优先级后，在隔离 fixture 先实现 exact-match relation projector。

### R3-C04 AP Group

- `SOURCE_FILE`：`Phase9VendorParsers.cs:160-161,186-197`。
- `EXISTING_API`：已提供 AC/domain 组分页、组详情/provenance 和可见成员只读 API。
- `DATA_MODEL`：`wireless_ap_groups` 保存稳定组 ID、AC/domain scope、成员 resource IDs、source revision 和 scope status。
- `AUTHORIZATION_MODEL`：三条 API 均复用服务端无线 Site claims predicate；详情和成员只从当前可见投影返回。
- `TEST_EVIDENCE`：`R3_DUAL_AC_GROUP_SCOPE=PASS`、`R3_DUAL_AC_GROUP_DETAIL_PROVENANCE=PASS`，并有无线浏览器组详情点击验收。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_READ_ONLY`。
- `CONTRACT_STATUS`：只读业务合同已接入，写回能力仍禁止。
- `MISSING_FIELDS`：长期审计事件和无线任务 subject 仍属于 G09/G10，不由 AP Group 详情接口补齐。
- `RISKS`：同名组跨 AC 混淆；组名变化造成历史成员漂移。
- `NEXT_ACTION`：依附 AC domain 建模，禁止全局 group name join。

### R3-C05 Serial/MAC Identity

- `SOURCE_FILE`：`Phase9VendorParsers.cs:160-164,199-220`；架构原则为 `04_DEVICE_AND_AC_DOMAIN_MODEL.md:5-18`。
- `EXISTING_API`：无 AP identity API；现有 mesh peer 有有限的 canonical MAC key，但不是 AP resource contract。
- `DATA_MODEL`：serial/MAC 只是 parser payload；`device_inventory.id` 只保证 AC 普通设备稳定 ID。
- `AUTHORIZATION_MODEL`：身份冲突尚无拒绝/待审核 API。
- `TEST_EVIDENCE`：现有 H3C/Mesh parser replay PASS；没有缺 serial、缺 MAC、多接口 MAC、更换设备、跨 AC duplicate fixture 的无线合同测试。
- `IMPLEMENTATION_STATUS`：`EVIDENCE_REQUIRED`。
- `CONTRACT_STATUS`：不足以宣布 AP 跨 AC 唯一身份可用。
- `MISSING_FIELDS`：规范化格式、证据等级、身份候选集合、冲突状态、替换/迁移规则、人工确认记录。
- `RISKS`：AP name、MAC 尾号、单一 AC 视角都可能产生错误合并。
- `NEXT_ACTION`：采用 `AP_RESOURCE` 与 observation 分离；serial/MAC 作为 exact evidence，不能单独越权合并。

### R3-C06 Runtime State

- `SOURCE_FILE`：`Phase9VendorParsers.cs:160-161`；`H3cFacts.cs:21-30,128-199`。
- `EXISTING_API`：facts API 原样返回 `status/source/capturedMs/isCurrent`，没有无线状态投影。
- `DATA_MODEL`：原始 `State` 可解析为 payload，但当前 `H3cFactSnapshot.Status` 主要是 `STALE/EMPTY/UNSUPPORTED/PARTIAL` 等事实状态。
- `AUTHORIZATION_MODEL`：无状态写接口；读取仍受 profile facts scope。
- `TEST_EVIDENCE`：parser/replay PASS；没有 R/M、R/B、I 冲突和失效测试。
- `IMPLEMENTATION_STATUS`：`DESIGNED_NOT_IMPLEMENTED`。
- `CONTRACT_STATUS`：R/M、R/B、I 尚未形成可验收合同。
- `MISSING_FIELDS`：raw state、normalized state、state source、observed_at、freshness deadline、confidence、conflict/unknown。
- `RISKS`：用状态字符串直接推导主备拓扑，或采集失败后错误保持在线。
- `NEXT_ACTION`：R3-B3 先冻结归一化映射和失效语义，R/M 只作为有证据的优先事实。

### R3-C07 Multi-AC Topology

- `SOURCE_FILE`：设计参考 `04_DEVICE_AND_AC_DOMAIN_MODEL.md:20-39`；当前源码无对应 domain/member runtime。
- `EXISTING_API`：无 stack/cluster/1+1 topology endpoint。
- `DATA_MODEL`：`device_inventory` 无逻辑 domain、成员角色、优先级或 observed member state。
- `AUTHORIZATION_MODEL`：只有设备 Site scope，不能表达 domain 聚合授权。
- `TEST_EVIDENCE`：无 multi-AC topology test；SmartHub 材料仅为 UI 参考，不能替代后端证据。
- `IMPLEMENTATION_STATUS`：`DESIGNED_NOT_IMPLEMENTED`。
- `CONTRACT_STATUS`：未准备好。
- `MISSING_FIELDS`：domain kind、member role、source revision、validity、conflict/partial、migration history。
- `RISKS`：把独立多 AC、堆叠、集群和 1+1 统一成普通主备。
- `NEXT_ACTION`：R3-B0/B5 先获得业务决策，再做 domain/member contract。

### R3-C08 Site Scope

- `SOURCE_FILE`：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`；`Program.cs:918-928`；`DeviceSiteScope.cs:8-35`。
- `EXISTING_API`：设备 inventory/group/CRUD 已使用 claims；H3C facts GET 有 `Resolve(CurrentIdentity)` 和 `H3cProfileIsInScope`；但 H3C profile list `Program.cs:877-878` 未提供无线 Site contract，task definition/run `Program.cs:953-989` 只做 SUPERADMIN 角色边界。
- `DATA_MODEL`：claims 来源 `web_user_site_memberships`，按 source+revision+validity/state 得到 authorized Site set。
- `AUTHORIZATION_MODEL`：R2 已实现 exact Site scope、过期/冲突 fail-closed、SUPERADMIN global；无线 AP 需要 AC-derived scope，不得默认任意 AC Site 并集。
- `TEST_EVIDENCE`：31/31 claims、P2B5 HTTP scope PASS；无无线 relation authorization tests。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：R2 authority 可复用；R3 subject-to-AC/AP relation authorization 缺失。
- `MISSING_FIELDS`：AC domain→Site source、AP visibility predicate、multi-site union/deny、group scope、audit Site/revision。
- `RISKS`：generic H3C profile/task API 绕过无线 claims；AP 被任意关联 AC 的 Site 泄露。
- `NEXT_ACTION`：R3-B1 先做 scoped adapter 和 negative tests，禁止无线接口直接复用无 scope 的 generic list。

### R3-C09 Task API

- `SOURCE_FILE`：`BoundedCollectionScheduler.cs:8-16,79-116`；`Catalog.cs:79-82,1325-1450,1649-1685`；`docs/DATABASE_DESIGN.md:36,45,59,67`。
- `EXISTING_API`：`/api/system/h3c/task-definitions`、`/api/system/tasks/{definitionId}/runs`、`GET /api/system/tasks`。
- `DATA_MODEL`：单 definition 是 target/profile + one command + timeout；run 有状态和 Raw output manifest，没有 parent plan/item/projection outcome。
- `AUTHORIZATION_MODEL`：H3C definition/run 为 SUPERADMIN；通用任务列表缺无线 Site business scope。
- `TEST_EVIDENCE`：Service harness、Raw/task recovery、cancel/timeout/restart tests PASS；未测试无线 business plan。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：平台采集能力可复用，不能称为无线任务合同。
- `MISSING_FIELDS`：plan scope snapshot、parent/item、attempt、parse/projection/verification、partial business outcome、Site/AC/AP subject。
- `RISKS`：UI 先做任务列表会把单命令成功误显示为 AP 业务成功。
- `NEXT_ACTION`：先按 Task Orchestration RFC 定义 plan/item adapter，保留 scheduler 不承载业务规则。

### R3-C10 Audit API

- `SOURCE_FILE`：`docs/DEVICE_AUDIT_MODEL.md:1-32`；`docs/DEVICE_DATA_MODEL.md:33`；Service harness audit tests。
- `EXISTING_API`：设备 mutation/batch/terminal/host-key/task 记录现有 `audit_events`；无无线业务事件 vocabulary/API。
- `DATA_MODEL`：现有安全 bounded detail，可记录 actor/operation/outcome，但无 AC/AP relation subject、source revision 和 Site。
- `AUTHORIZATION_MODEL`：R2 角色/CSRF/claims 失败会审计；无线刷新、冲突确认、关系人工裁决尚未定义。
- `TEST_EVIDENCE`：claims E2E 的 `AUDIT_VISIBLE`、Service harness audit PASS；无无线事件测试。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：通用审计可复用，wireless audit contract 缺。
- `MISSING_FIELDS`：wireless action、AC/domain/AP IDs、Site claim revision、source observation/relation revision、safe reason code。
- `RISKS`：无法追溯谁在何 Site 看到/确认了冲突关系。
- `NEXT_ACTION`：R3-B7 增加只读刷新/人工确认/冲突拒绝事件，敏感命令和原始输出不写 audit。

### R3-C11 LLDP

- `SOURCE_FILE`：`H3cComware.cs:20-28,396-408`；`Phase9VendorParsers.cs:170-171,320-354`；`H3cFacts.cs:181-184`。
- `EXISTING_API`：LLDP 命令在 H3C allowlist 和 parser 中；事实可通过 H3C facts 查询。
- `DATA_MODEL`：`localInterface`、neighbor system/interface/management address/chassis ID 是 fact payload；无 AP relation projection。
- `AUTHORIZATION_MODEL`：沿 H3C profile facts scope；不具备跨域/无线业务授权语义。
- `TEST_EVIDENCE`：H3C live fixture/parser/replay harness PASS，均为已有/离线证据；没有 AP relation test。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：可作为 relation 的辅助证据，不足以单独确认 AP identity 或拓扑。
- `MISSING_FIELDS`：match method、candidate set、conflict code、relation revision、verification level。
- `RISKS`：用邻居名字/端口模糊匹配写回 AP/Device master。
- `NEXT_ACTION`：只作为 exact evidence 输入，冲突返回 `UNKNOWN/CONFLICT`，不覆盖主数据。

### R3-C12 History

- `SOURCE_FILE`：`H3cFacts.cs:45-123`；架构规则 `04_DEVICE_AND_AC_DOMAIN_MODEL.md:41-59`。
- `EXISTING_API`：facts API 的 `history` 查询和 `is_current`；没有 `PreviousEffective`/`Recent10` AP projection。
- `DATA_MODEL`：导入时同 profile/kind/identity 清旧 current、插入新 current；按事实身份保存历史，但没有无线有效变化事件规则。
- `AUTHORIZATION_MODEL`：facts GET 做 Site scope；历史仍是 profile facts 范围。
- `TEST_EVIDENCE`：Service harness current/history 和 failed-run preservation PASS；无 AP runtime state history test。
- `IMPLEMENTATION_STATUS`：`IMPLEMENTED_PARTIAL`。
- `CONTRACT_STATUS`：底层事实历史可复用，业务动态历史未准备好。
- `MISSING_FIELDS`：effective value hash、PreviousEffective、Recent10 cap、failure/current separation、source revision/observation lineage。
- `RISKS`：失败采集覆盖 Current、未变化重复写入、历史事实无法解释来源。
- `NEXT_ACTION`：按 Next 已冻结的 Current + PreviousEffective + Recent10 语义为 AP projector 单独定义。

## 真实来源与证据边界

| 层 | 当前真实来源 | 本轮结论 |
| --- | --- | --- |
| AC 资产 | `device_inventory` + `site_id` + optional H3C profile | 可导航入口，不是 AC domain |
| AP 采集输入 | H3C allowlist 命令及离线 replay fixture | parser/fact 输入存在，无实时无线业务 API |
| AP identity | parser serial/MAC/name/id payload | 只有证据字段，没有跨 AC 主身份 |
| AP 状态 | parser raw `State` + H3cFact status | 未形成 R/M/R/B/I normalized contract |
| AP 组 | parser `Group` 字段 | 不是 group resource |
| 邻居 | H3C LLDP parser/fact | 只能做 exact evidence |
| 权限 | P2B5 `web_user_site_memberships` | 可复用，但需无线 subject adapter |
| 任务/Raw | `task_definitions`/`task_runs`/Raw manifests | 单命令平台能力，不是无线 business plan |

