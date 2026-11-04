# R3-B0 R3 实施批次计划

状态：`PROPOSED_PENDING_APPROVAL`。这是批准后的实施顺序，不是本轮实施授权。

## 1. 批次总览

| 批次 | 内容 | 主要输出 | 前置 | 当前结论 |
| --- | --- | --- | --- | --- |
| R3-B1 | AC domain 与 typed AP read model | domain/AP resource/observation service + read API | 7 decisions approved；B0 contract | 首个实现批次，当前未开始 |
| R3-B2 | identity evidence 与 AC↔AP association | serial/MAC normalization、candidate/conflict、relation projection | B1 + DECISION 02/03 | 未开始 |
| R3-B3 | runtime state/freshness/history | R/M/R/B/I mapping、state conflict、bounded history | B2 + DECISION 05 | 未开始 |
| R3-B4 | AC-scoped AP group | group read API/membership projection | B2 + DECISION 04 | 未开始 |
| R3-B5 | Site authorization projection | wireless predicates、partial scope、snapshot/current checks | B1-B4 + DECISION 03/06 | 未开始 |
| R3-B6 | wireless task/report contract | plan/item/run subject、bounded scheduler adapter、read/download auth | B5 + DECISION 06 | 未开始 |
| R3-B7 | audit/provenance and UI vertical slice | manifest/hash refs、audit query、R3 UI vertical slice | B1-B6 + DECISION 07 | 未开始 |

## 2. R3-B1 入口与退出条件

首个产品实现批次为 `R3-B1`，不是 B0 文档批次。入口必须同时满足：

1. 7 项 decision 均有用户明确 `OPTION_A/B/C/DEFER` 回复；
2. 业务决策 JSON/Markdown 的 policy 从 `PROPOSED` 更新为用户确认后的状态；
3. schema/model/migration 设计通过单独评审，且不把 B0 文档直接当 migration；
4. fixture F01-F05 和 SC-01..05 变成可执行隔离测试；
5. Site authority 仍唯一复用 P2B5，不新增旁路 claims。

R3-B1 退出条件：

- AC domain 只引用现有 device inventory IDs；
- AP list/detail 仅返回 typed projection 和 visible observations；
- identity unknown/conflict 不被伪装为 confirmed；
- unauthorized/partial scope 场景有可断言的响应和 no-leak 测试；
- 失败不会清掉上一有效 Current；
- product code、schema 和 migration 的变更均有独立 diff/测试记录。

## 3. 批次治理

- 每批次只实现一个业务边界，禁止为无线功能顺手重构 R2 device management。
- 每批次必须保留 source manifest/hash/revision 和测试 fixture，不能接入真实 AC/AP。
- 任何 write-back、AP group move、network scan、real task execution 需要重新授权，不由本计划隐含授权。
- 失败、stale、conflict 是一等结果，不通过“最新字符串胜出”消除。

## 4. 当前准入判断

`GATE_F_IMPLEMENTATION_READINESS=PARTIAL`：合同和测试入口已具备，但 7 项用户决策尚未批准，且产品实现尚未开始。`R3_FIRST_IMPLEMENTATION_BATCH=R3-B1`。
