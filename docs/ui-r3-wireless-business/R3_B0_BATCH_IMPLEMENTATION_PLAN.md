# R3-B0 R3 实施批次计划（修订版）

## 2026-11-07 implementation update

R3 decisions are `APPROVED/FROZEN`. B1 through the current read-model slice are implemented partially in product code, including schema v24 persistence, AC/AP projection, identity/runtime/group services, claims-guarded routes and Wireless UI. B6 wireless business task execution and full 57-scenario acceptance remain pending. The plan below is retained as the original batch contract and exit criteria.

修订日期：2026-11-04
状态：`FROZEN_DESIGN_NOT_IMPLEMENTED`。冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。这是批准后的实施顺序，不是本轮实施授权。

冻结更新：7/7 业务决策已冻结，R3-B1 的接口和安全范围已准备；产品编码、schema/migration、真实 collection 和无线 UI 仍需单独授权。第一条无线 route 必须先通过 Site claims predicate；API-W09 仍为内部 P2B5 predicate。

## 1. 批次总览

| 批次 | 内容 | 主要输出 | 前置 | 当前结论 |
| --- | --- | --- | --- | --- |
| R3-B1 | AC domain、typed AP read model 与基础授权 | domain/AP resource/observation service + W01/W02/W03 + P2B5/no-leak tests | 7 decisions approved；B0 contract；schema/migration separately authorized | 首个实现批次，当前未开始 |
| R3-B2 | identity evidence、AC↔AP association 与授权扩展 | serial/MAC normalization、candidate/conflict、relation projection、W04 | B1 + DECISION 02/03 | 未开始 |
| R3-B3 | runtime state/freshness/history 与状态授权 | R/M/R/B/I mapping、state conflict、bounded history、W05/W06 | B2 + DECISION 05 | 未开始 |
| R3-B4 | AC/domain-scoped AP group 与 group authorization | Domain/AC group scope、membership projection、W07/W08 | B2 + DECISION 04 | 未开始 |
| R3-B5 | 高级 Site authorization 专项闭环 | cross-AC、partial visibility、complex aggregate、claims revision、no-leak regression；W09 继续内部复用 P2B5，不新增 route | B1-B4 + DECISION 03/06 | 未开始 |
| R3-B6 | wireless task/report contract | plan/item/run subject、bounded scheduler adapter、execution/read/download auth、W10/W11 | B5 + DECISION 06 | 未开始 |
| R3-B7 | audit/provenance and UI vertical slice | manifest/hash refs、audit query、W12、R3 UI vertical slice | B1-B6 + DECISION 07 | 未开始 |

## 2. R3-B1 新入口门禁

R3-B1 不是“先做资源、以后再补权限”。API-W01/W02/W03 的第一条实现 route 必须同时具备：

1. 7 项 decision 均有用户明确 `OPTION_A/B/C/DEFER` 回复；
2. 业务决策 JSON/Markdown 的 policy 从 `PROPOSED` 更新为用户确认后的状态；
3. AC inventory 仍只引用既有 `device_inventory.id`，没有第二套资产 authority；
4. 每条 route 服务端复用 P2B5 Site claims，验证 AC scope，过滤 AP observations，防 serial/MAC/opaque-ID bypass，并对 scope failure fail closed；
5. list 不泄露未经授权对象 count/total/name/time，detail 对不可见对象返回不可区分的 `RESOURCE_NOT_VISIBLE`；
6. API-W09 只作为内部 P2B5 predicate，不新增 claims 暴露接口；
7. schema/model/migration 设计通过单独评审，且不把 B0 文档直接当 migration；
8. F01-F05、F11、F13-F17 和 SC-01..05、SC-24..27、SC-36..57 具备可执行隔离测试设计；
9. Site authority 仍唯一复用 P2B5，不新增旁路 claims。

## 3. R3-B1 退出条件（未来实现后才可判断）

```text
AC_DOMAIN_IDENTITY=PASS
AC_INVENTORY_REFERENCE=PASS
AP_RESOURCE_PROJECTION=PASS
AP_OBSERVATION_PROVENANCE=PASS
SITE_AUTHORIZATION=PASS
CROSS_SITE_DENIAL=PASS
UNAUTHORIZED_RESOURCE_LEAK=NO
IDENTITY_CONFLICT_HANDLING=PASS
TEST_FIXTURES=PASS
REGRESSION=PASS
```

上述结果在 B0 不得填写为 PASS；当前只是 exit criteria。

## 4. 权限撤销的实现前置

所有未来 task/report 代码都必须使用：

```text
EFFECTIVE_EXECUTION_TARGETS =
  CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS
```

每个 connect、command/item、retry、resume、report publish 边界重新检查 claims。撤销后不启动未开始目标；对不可安全中断的单个 bounded operation 只完成当前原子边界或安全中止，封存 partial evidence，标记 `REVOKED_AFTER_START`，不启动后续操作。完成后的 result/detail/list/download/audit 仍按当前 claims fail closed。

## 5. 批次治理

- 每批次只实现一个业务边界，禁止为无线功能顺手重构 R2 device management。
- 每批次必须保留 source manifest/hash/revision 和测试 fixture，不能接入真实 AC/AP。
- 任何 write-back、AP group move、network scan、real task execution 需要重新授权，不由本计划隐含授权。
- 失败、stale、conflict、revoked 是一等结果，不通过“最新字符串胜出”消除。
- 新增 Site claims 不能扩大旧任务的 snapshot target set。

## 6. 当前准入判断

`GATE_F_IMPLEMENTATION_READINESS=PARTIAL`：合同、撤销语义、首批授权门禁和测试入口已具备，但 7 项用户决策尚未批准，且产品实现尚未开始。`R3_FIRST_IMPLEMENTATION_BATCH=R3-B1`。
