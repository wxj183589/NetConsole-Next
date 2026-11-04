# R3-B0 API Gap 追踪矩阵

状态：`DESIGN_ACCOUNTED_NOT_IMPLEMENTED`。上一轮确认有 10 个 gap；本轮为每个 gap 指定业务对象、合同、测试和实施批次，但没有宣称 gap 已被产品代码关闭。

| Gap | 上一轮缺口 | 本轮合同/endpoint | 授权/数据边界 | 测试 | 计划批次 | 当前状态 |
| --- | --- | --- | --- | --- | --- | --- |
| G01 | AC domain/member resource absent | `R3_B0_AC_AP_IDENTITY_MODEL.md`；API-W01 | AC inventory ID 是来源，domain 不复制资产 | SC-01..05, SC-22 | R3-B1 | 设计完成，未实现 |
| G02 | typed AP resource/observation absent | API-W02/W03；identity model | AP resource 不是 parser 行，observation 带 source | SC-06..12, SC-23..25 | R3-B1 | 设计完成，未实现 |
| G03 | AC↔AP association absent | API-W04；multi-AC model | 每个 source AC 独立 relation/revision | SC-13..15, SC-26 | R3-B2 | 设计完成，未实现 |
| G04 | serial/MAC normalization/conflict absent | AP identity evidence contract | exact evidence，缺失/冲突不猜 | SC-06..12 | R3-B2 | 设计完成，未实现 |
| G05 | normalized R/M/R/B/I state absent | API-W05；state model | raw 保留，失败不等 offline，stale/conflict 分离 | SC-16..23 | R3-B3 | 设计完成，未实现 |
| G06 | multi-AC topology absent | API-W06；domain topology contract | 只由 member/HA/IRF/cluster evidence 确认 | SC-03..05, SC-24 | R3-B3 | 设计完成，未实现 |
| G07 | AP group resource absent | API-W07/W08；group contract | AC/domain-owned read-only，write out of scope | SC-27..29 | R3-B4 | 设计完成，未实现 |
| G08 | wireless Site predicate absent | API-W09；Site authorization contract | 复用 P2B5 claims，四层 predicate，fail closed | SC-22..27, SC-30 | R3-B5 | 设计完成，未实现 |
| G09 | wireless task/plan/audit subject absent | API-W10/W11；task scope snapshot | create snapshot + read current claims | SC-31..34 | R3-B6 | 设计完成，未实现 |
| G10 | AP history/provenance projection absent | API-W12；audit/provenance clauses | manifest/hash/reference only，无 raw/credential | SC-35, SC-20, SC-30 | R3-B7 | 设计完成，未实现 |

## 追踪结论

- `API_GAPS_BEFORE=10`。
- `API_GAPS_ACCOUNTED_FOR=10`：每个 gap 均有合同、授权边界、测试和批次映射。
- `API_GAPS_REMAINING=10`：没有 product code、service、route、schema 或 migration 改动，本轮只是 design accounting。
- 现有 `GET /api/system/devices`、`GET /api/system/h3c/profiles/{id}/facts`、generic task routes 和 P2B5 scope 仍为可复用边界，不能被重命名为已完成的 wireless API。

## 证据索引

- API 现状：`src/NetConsole.Next.Service/Program.cs:307-315,360-363,659,877-928,953-989`。
- parser/facts 现状：`src/NetConsole.Next.Service/Phase9VendorParsers.cs:160-220`、`src/NetConsole.Next.Service/H3cFacts.cs:8-30,103-123`。
- Site authority：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`、`src/NetConsole.Next.Service/Program.cs:1068-1124`。
