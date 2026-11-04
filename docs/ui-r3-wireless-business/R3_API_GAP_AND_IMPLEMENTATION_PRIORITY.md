# R3 API 缺口与实施优先级

日期：2026-11-04

## 2026-11-05 冻结更新

七项 R3 业务决策已冻结，10 个 API gap 仍全部未实现。R3-B1 只准备 AC Domain、Typed AP Resource、Observation 和基础 Site claims 的只读接口；第一条 route 即过 P2B5 predicate，API-W09 不新增 route。产品实现、schema/migration 和无线 UI 均未开始。

本计划依据当前源码和 R0-R1 task RFC 调整；本轮只记录顺序，不实施 R3 产品代码。

## 1. 缺口清单

`R3_API_GAPS=10`

| GAP | 缺口 | 证据 | 优先级 |
| --- | --- | --- | --- |
| G01 | AC domain/member 不存在 | `device_inventory` 只有普通 AC 设备；无 `AcDomain` runtime | P0 |
| G02 | typed AP resource/observation 不存在 | parser/replay 存在，AP business API 不存在 | P0 |
| G03 | AC↔AP association 不存在 | 无 binding relation、revision、conflict API | P0 |
| G04 | serial/MAC normalization/conflict 不存在 | serial/MAC 仅在 parser payload | P0 |
| G05 | R/M/R/B/I normalized state 不存在 | parser raw State，无 freshness/confidence projection | P0 |
| G06 | multi-AC topology 不存在 | 无 stack/cluster/1+1 domain/member contract | P0 |
| G07 | AP group resource 不存在 | AP row 有 Group 字段，无 AC-scoped group API | P1 |
| G08 | wireless Site predicate 不存在 | R2 claims 存在，但 generic profile/task 路由不构成无线授权 | P0 |
| G09 | wireless plan/task/audit subject 不存在 | scheduler 是单 target/command；无 business item/audit vocabulary | P1 |
| G10 | AP history/provenance projection 不存在 | `h3c_facts` 只有 current/history，未实现 Recent10/effective semantics | P0 |

## 2. 推荐实施批次

### R3-B0：API 与数据事实确认（当前首批，但尚未具备实现条件）

- 输入条件：批准本报告第 3 节的 7 项业务决策；锁定 `AcDomain`、`ApResource`、observation、binding 和 state vocabulary。
- API 前置依赖：无新生产路由；先建立离线 contract fixture 和响应 envelope。
- 数据来源：现有 H3C parser/replay + 脱敏 multi-AC fixture；不连接真实 AC/AP。
- 权限边界：fixture 按 Site/AC/observation 建立负向样例，复用 P2B5 claims authority。
- 测试要求：identity conflict、multi-AC、state conflict、failure preservation、scope denial、idempotency。
- 验收标准：每个字段有来源、时间、revision、verification、失败语义；未知不会被显示成在线/成功。
- 当前阻断：7 项用户决策未批准；当前无 AP business schema/API。
- 是否需要用户批准：是，全部业务语义需批准。

### R3-B1：AC Inventory 和 Site Scope

- 输入条件：B0 完成；AC 仍引用 `device_inventory.id`。
- API 前置依赖：scoped AC list/detail，返回 Site、claims revision、pagination/filter；禁止复用无 scope profile list。
- 数据来源：现有 `device_inventory`、`sites`、`web_user_site_memberships`、H3C profile linkage。
- 权限边界：exact Site claims、SUPERADMIN global、过期/冲突 fail-closed。
- 测试要求：单/多 Site、cross-site denial、profile/task no-leak。
- 验收标准：AC 不产生 duplicate identity，所有响应带 scope summary。
- 当前阻断：无线 adapter 未实现。
- 是否需要用户批准：只需确认 B0 的 AC domain mapping。

### R3-B2：AP Identity 与 AC↔AP 关联

- 输入条件：B0/B1 完成，identity precedence 已批准。
- API 前置依赖：AP resource、candidate/conflict、AC observation/binding 查询。
- 数据来源：H3C AP all/address/radio parser、Raw manifest、observation revision。
- 权限边界：按来源 AC/domain Site，AP resource 跨 AC 但 observation 不越权。
- 测试要求：同 serial、多 MAC、缺字段、replacement、duplicate cross-AC、conflict。
- 验收标准：同一物理 AP 不重复建资产；不能 exact match 时明确 conflict/unknown。
- 当前阻断：无持久化/API/schema，不能只做 UI。
- 是否需要用户批准：需要 identity/association 规则批准。

### R3-B3：AP Runtime State

- 输入条件：B2 relation 和 observation 可追溯。
- API 前置依赖：raw/normalized state、freshness、confidence、Current/PreviousEffective/Recent10。
- 数据来源：H3C raw State + captured time + verification + task item result。
- 权限边界：按 AP observation visibility；失败不扩大/清除授权。
- 测试要求：R/M/R/B/I、unknown/stale/conflict、time order、failed collection preservation。
- 验收标准：不以字符串单独推拓扑，不把未采集显示在线。
- 当前阻断：状态映射和 freshness 尚未批准/实现。
- 是否需要用户批准：是，至少 state mapping/deadline。

### R3-B4：AP Group

- 输入条件：B2 AP resource/binding；group ownership 已确定。
- API 前置依赖：AC-scoped group list/members/status/revision。
- 数据来源：H3C AP Group observation，保留 source AC。
- 权限边界：group 只在授权 AC/domain Site 可见。
- 测试要求：同名跨 AC、成员变化、空组、跨 Site denial。
- 验收标准：无全局 name join，无跨 AC 隐式合并。
- 当前阻断：group key/ownership pending。
- 是否需要用户批准：需要。

### R3-B5：多 AC 拓扑与冲突处理

- 输入条件：B2/B3；domain kind/member evidence 已批准。
- API 前置依赖：domain/member/topology/conflict endpoints。
- 数据来源：明确 AC member/HA/IRF/cluster evidence；AP state 仅为辅助。
- 权限边界：只返回授权 domain/member/observation。
- 测试要求：独立、堆叠、集群、1+1、冲突、partial domain。
- 验收标准：没有成员证据就不显示已确认 topology。
- 当前阻断：当前源码完全缺失。
- 是否需要用户批准：需要。

### R3-B6：无线业务只读 UI

- 输入条件：B1-B5 API contracts、fixtures、negative tests 完成。
- API 前置依赖：分页/筛选/状态/组/详情/来源/空态/错误/权限 API 全部稳定。
- 数据来源：只读 typed API；SmartHub 仅作为 clean-room 布局参考。
- 权限边界：UI 不复制 claims 判断，所有结果由服务端授权。
- 测试要求：1920/2560/125%（若环境可用）、窄视口、loading/empty/error/conflict/stale。
- 验收标准：界面不能把 mock/opaque fact 当真实无线业务成功。
- 当前阻断：前置 API/data contract 未实现。
- 是否需要用户批准：需要 UI scope 和验收场景确认。

### R3-B7：任务与审计集成

- 输入条件：B1-B5 与只读 UI contract 完成。
- API 前置依赖：business plan/item、Raw/report manifest、audit subject、cancel/retry/partial semantics。
- 数据来源：bounded scheduler + durable Raw + typed projection。
- 权限边界：task scope snapshot + current read authorization + audit Site。
- 测试要求：cancel/timeout/restart/partial/failure/current preservation/no secret leakage。
- 验收标准：任务成功不等于无线业务成功，business outcome 必须由 item/projection 计算。
- 当前阻断：现有 scheduler 只有单 target/command。
- 是否需要用户批准：需要 retention/report/task visibility 决策。

## 3. 第一批是否已具备条件

`R3_FIRST_IMPLEMENTATION_BATCH=R3-B0`

但 `R3-B0_IMPLEMENTATION_READY=NO`。原因不是技术无法开始，而是核心身份、授权和状态语义仍有 7 项用户决策待确认。当前适合做的下一步是批准合同并补充离线脱敏 fixture/测试计划；不适合直接新增无线页面或 schema。

