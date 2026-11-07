# R3-B0 最终准入报告

## 2026-11-07 R6.6/R3 continuation update

The seven decisions are now `APPROVED/FROZEN` under the amended OPTION_A package. The repository has since implemented a partial R3 read path: schema v24 wireless projection persistence, AC/AP typed read models, identity lifecycle boundaries, runtime state projection, AP group projection, Site claims guards and the Wireless UI. These capabilities are covered by isolated fixtures and service/browser tests.

Current implementation status remains **PARTIAL**. Wireless task-plan execution, authenticated simulator collection, HA/failover evidence, Windows 125% acceptance and the full 57-scenario business gate are still open. This update supersedes the historical pre-approval statements below without rewriting their audit history.

日期：2026-11-04  
基线：`b3f1a2d6303e5196c2aced62afb7ee8a1b8e9362`  
分支：`codex-A/netconsole-next-r2-device-management-ui`  
状态：`PARTIAL`，原因是 7 项用户决策未批准、10 个 API gap 仍未实现。

## 1. 本轮完成

- 将上一轮审计的 7 项 pending decisions 固化为唯一 ID `R3_DECISION_01..07`，形成 Markdown/JSON 决策包。
- 建立 AC、AcDomain、AP resource、physical identity、observation、group、task scope 和 audit provenance 的 proposed contracts。
- 将 G01-G10 全部映射到 API contract、authorization boundary、test scenarios 和 implementation batches。
- 设计 35 个测试场景和 12 个最小隔离 fixture；不运行真实设备、网络扫描或真实任务。
- 明确 `R3-B1` 是第一个产品实现批次；本轮没有产品代码、schema、migration 或 UI 修改。

## 2. Gate 结果

| Gate | 结果 | 说明 |
| --- | --- | --- |
| A Business decisions | `PARTIAL` | 7/7 pending，推荐未转批准 |
| B Data model | `PARTIAL` | 分层和字段合同已设计，未落模型/schema |
| C API contract | `PARTIAL` | 10 gaps 已逐项设计 accounting，0 个由本轮实现关闭 |
| D Authorization | `PARTIAL` | 复用 P2B5 并定义 wireless predicates，未实现新 projection |
| E Test readiness | `PASS` | 35 场景、12 fixture、验收断言已设计；本轮仅文档验证 |
| F Implementation readiness | `PARTIAL` | R3-B1 顺序和退出条件明确，等待决策批准 |

## 3. 未完成与阻塞

1. 用户没有逐项批准 `R3_DECISION_01..07`，故 policy 仍是 `PROPOSED`。
2. G01-G10 仍是 product implementation gaps；`API_GAPS_REMAINING=10`。
3. R3 wireless UI implementation 未开始。
4. R2 final status 仍为 `PARTIAL`；Windows native 125% acceptance 仍 `PENDING`，本轮不重跑。
5. 真实 AC/AP、真实登录、网络扫描、真实任务执行均未授权且未执行。

## 4. 建议用户回复

请逐项回复 `R3_DECISION_01..07=OPTION_A/B/C/DEFER`。收到后应重新生成 JSON/Markdown 状态、确认哪些 recommendation 被接受，再决定是否授权 R3-B1。未收到前不应把任何 proposed contract 作为 frozen product behavior。

## 5. 变更与数据声明

- `R3_PRODUCT_CODE_CHANGED=NO`
- `R3_WIRELESS_UI_IMPLEMENTATION=NO`
- `NEXT_DATABASE_SCHEMA_CHANGED=NO`
- `NEXT_DATABASE_MIGRATION_CHANGED=NO`
- `NEXT_AC_AP_MODEL_CHANGED=NO`
- `R2_SITE_CLAIMS_CHANGED=NO`
- `R2_DEVICE_MANAGEMENT_CHANGED=NO`
- `PRODUCTION_DATA_CHANGED=NO`
- `REAL_DEVICE_ACCESS=NO`
- `REAL_TASK_EXECUTION=NO`
- `PUSH_STATUS=NOT_PUSHED`
- `BRANCH_MERGE=NO`
- `RELEASE_PUBLISH=NO`

## 6. 依据

详见：

- `R3_B0_BUSINESS_DECISION_PACKAGE.md` / `.json`
- `R3_B0_AC_AP_IDENTITY_MODEL.md`
- `R3_B0_MULTI_AC_ASSOCIATION_AND_STATE_MODEL.md`
- `R3_B0_AP_GROUP_CONTRACT.md`
- `R3_B0_SITE_AUTHORIZATION_CONTRACT.md`
- `R3_B0_API_CONTRACT_SPEC.md`
- `R3_B0_API_GAP_TRACEABILITY_MATRIX.md`
- `R3_B0_TEST_SCENARIO_MATRIX.md`
- `R3_B0_BATCH_IMPLEMENTATION_PLAN.md`

## Amendment 2026-11-04

本轮 amendment 追加以下正式规则：

- AP Group 只有在 domain-shared evidence 明确存在时才使用 `DOMAIN|ac_domain_id|source_group_key`；否则使用 `AC|source_ac_device_id|source_group_key`，缺 source key/ID 不合并。
- Task scope snapshot 只限制最大目标集合；实际执行、恢复、读取、下载、报告和审计均重新计算 `CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS`。
- 对不可安全中断的当前 bounded operation，只完成当前原子边界或安全中止，封存 partial evidence 并标记 `REVOKED_AFTER_START`，不启动后续操作。
- R3-B1 从第一条无线资源 route 开始复用 P2B5 Site claims；R3-B5 只做高级跨 AC/partial/no-leak 闭环。
- API-W09 改为内部 P2B5 predicate，不新增 Site claims route。
- 测试设计从 35 场景/12 fixture 扩展为 57 场景/17 fixture；本轮产品测试仍为 `NOT_RUN_DOC_ONLY`。

详细修订见 `R3_B0_CONTRACT_AMENDMENT_20261104.md`、`R3_B0_FINAL_USER_APPROVAL_SHEET.md` 和 `R3_B0_AMENDED_READINESS_REPORT.md`。7 项决策仍为 `0 approved / 7 pending`，policy 仍为 `PROPOSED`。
