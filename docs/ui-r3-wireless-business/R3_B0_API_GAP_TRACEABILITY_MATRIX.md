# R3-B0 API Gap 追踪矩阵（修订版）

## 2026-11-06 G07 当前切片追踪

当前状态：`LOCAL_ACCEPTANCE_PASS`。下方 10 gap、implemented=0 与“parser only has Group text”等表述保留为冻结设计/早期实现批次历史，不代表当前 G07 来源身份实现；本节不重算或关闭其他 gap。

| Gap / API | 当前实现范围 | 授权/数据边界 | 验证映射 | 剩余范围 |
| --- | --- | --- | --- | --- |
| G07 / API-W07/W08 | typed source-group evidence；AC/DOMAIN scoped key；名称 only 为 AC candidate/null key；成员 revision 独立；detail/members 保留实际 observation provenance | 现有 Site/source AC predicate；schema24 仅可重建 projection，Raw/Facts 为 Authority | SC-28/29/36..41；`WirelessGroupTests.cs`、API E2E、WirelessView 单测与浏览器脚本；均 `LOCAL_ACCEPTANCE_PASS` | 完整 Domain/group 生命周期、持久 change events、真实设备与其余全链路验收 |

本机来源身份切片已通过：10 个 Group 合同、Wireless projection、Runtime 13/13、schema24 迁移边界、真实 HTTP/离线 fixture 全部 16 个 marker、完整 Service harness、Chrome API mocked 15 状态、前端 9 files/29 tests 与 typecheck/独立 Vite；范围见 [本机验收记录](R3_FULL_CHAIN_FINAL_ACCEPTANCE.md)。具体字段/nullability、Domain shared 成员一致门槛和历史名称迁移见 [AP Group 当前合同](R3_B0_AP_GROUP_CONTRACT.md)。其他 G01..G10 状态须按各自当前证据判定，不因 G07 本切片自动提升。

## 历史 Gap 设计与早期实现追踪


修订日期：2026-11-04
状态：`FROZEN_DESIGN_ACCOUNTED_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。10 个 gap 均有业务字段、合同、授权、测试和实施批次追踪；没有 gap 被产品代码关闭。

冻结更新：`API_GAPS_TOTAL=10`、`API_GAPS_ACCOUNTED_FOR=10`、`API_GAPS_IMPLEMENTED_THIS_ROUND=0`、`API_GAPS_REMAINING=10`。

| GAP_ID | CAPABILITY | CURRENT_IMPLEMENTATION | PROPOSED_API | AUTHORIZATION | DATA_MODEL | BUSINESS_DECISION | TEST_CASES | IMPLEMENTATION_BATCH | STATUS |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| G01 | AC domain/member | AC 仍是 `device_inventory`，无 AcDomain runtime | API-W01 | P2B5 AC scope from first route | AcDomain/member logical projection | DECISION_01 | SC-01..05 | R3-B1 | `DESIGNED_NOT_IMPLEMENTED` |
| G02 | typed AP resource/observation | parser/facts payload，非 typed AP resource | API-W02/W03 | visible source observation only | ApResource/Observation | DECISION_02/03 | SC-06..15,23 | R3-B1/B2 | `DESIGNED_NOT_IMPLEMENTED` |
| G03 | AC↔AP association | read model now exposes source-bound association rows | API-W04 | source AC/domain Site predicate | relation revision/binding | DECISION_03 | SC-13..15,26 | R3-B2 | `IMPLEMENTED_READ_ONLY` |
| G04 | serial/MAC identity | parser fields，无 identity resolver | API-W02/W03 | serial/MAC search cannot bypass claims | exact evidence/candidate/conflict | DECISION_02 | SC-06..12,55..56 | R3-B2 | `DESIGNED_NOT_IMPLEMENTED` |
| G05 | normalized runtime state | generic fact status/current，无 wireless normalized state | API-W05 | only visible observations aggregate | raw/normalized/freshness/conflict | DECISION_05 | SC-16..23 | R3-B3 | `DESIGNED_NOT_IMPLEMENTED` |
| G06 | multi-AC topology | 无 domain topology projection | API-W06 | visible member/domain scope | topology evidence/revision | DECISION_01 | SC-03..05,24 | R3-B3 | `DESIGNED_NOT_IMPLEMENTED` |
| G07 | AP group resource | parser only has Group text | API-W07/W08 | group source predicate; no client scope override | Domain/AC group identity key + membership | DECISION_04 | SC-27..29,36..41 | R3-B4 | `IMPLEMENTED_READ_ONLY` |
| G08 | wireless Site predicate | P2B5 protects existing routes; no wireless predicate | API-W09 internal P2B5 reuse; no new route | server-side P2B5 from W01 onward | source AC/Site/revision projection | DECISION_03/06 | SC-22..27,49,53..57 | R3-B1/B5 | `DESIGNED_NOT_IMPLEMENTED` |
| G09 | wireless task/report/audit subject | generic task history/one profile command | API-W10/W11; generic route cannot bypass | creation snapshot ∩ current claims at every boundary | task subjects/items/scope outcome | DECISION_06 | SC-31..34,42..53 | R3-B1/B6 | `DESIGNED_NOT_IMPLEMENTED` |
| G10 | AP history/provenance | Raw manifest/task output exists; wireless projection absent | API-W12 | current Site subject predicate | manifest/hash/reference projection | DECISION_07 | SC-20,30,35 | R3-B7 | `DESIGNED_NOT_IMPLEMENTED` |

## 追踪结论

- `API_GAPS_BEFORE=10`。
- `API_GAPS_ACCOUNTED_FOR=10`：每个 gap 均有 capability、current implementation、proposed API、authorization、data model、business decision、test cases、implementation batch 和 status。
- `API_GAPS_IMPLEMENTED_THIS_ROUND=0`。
- `API_GAPS_REMAINING=10`：没有 product code、service、route、schema 或 migration 改动，本轮只是 design accounting。
- 现有 `GET /api/system/devices`、`GET /api/system/h3c/profiles/{id}/facts`、generic task routes 和 P2B5 scope 仍为可复用边界，不能被重命名为已完成的 wireless API。
- API-W09 仅代表内部 `P2B5_SCOPE_PREDICATE`，不新增 `/api/system/wireless/site-claims` route。

## 证据索引

- API 现状：`src/NetConsole.Next.Service/Program.cs:307-315,360-363,659,877-928,953-989`。
- parser/facts 现状：`src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-220`、`src/NetConsole.Next.Service/H3cFacts.cs:8-30,103-123`。
- Site authority：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`、`src/NetConsole.Next.Service/Program.cs:1068-1124`。
- Task/Raw boundary：`src/NetConsole.Next.Service/BoundedCollectionScheduler.cs:300-325`、`src/NetConsole.Next.Service/RawSegmentWriter.cs:142-148`、`src/NetConsole.Next.Service/Catalog.cs:1454-1491`。
