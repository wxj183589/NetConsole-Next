# R3-B0 Amendment 最终准备度报告

日期：2026-11-04  
基线：`1b4b9742a62e76839174c7f5425e0857b63e6201`  
分支：`codex-A/netconsole-next-r2-device-management-ui`  
结论：`PARTIAL`，本轮完成合同修订和批准准备，不开始 R3-B1。

## 1. 修订结果

- AP Group 现在明确 Domain-owned/AC-owned 双层 identity scope；Domain key 需要明确 domain-shared evidence，未证实时按 AC scope 或 unknown。
- Task/Report scope 明确 snapshot 是目标上限，不是永久授权；实际执行和读取使用 current claims intersection；撤销和不可安全中断 bounded operation 有明确状态。
- R3-B1 从 API-W01/W02/W03 第一条无线 route 开始执行 P2B5 server-side authorization；R3-B5 改为高级跨 AC/partial/no-leak 闭环。
- API-W09 改为内部 `P2B5_SCOPE_PREDICATE`，不新增 `/api/system/wireless/site-claims` route。
- 决策包 Markdown/JSON 增加 `OPTION_C`、`SECURITY_CONDITIONS`、`SCHEMA_IMPACT`、`APPROVAL_STATUS` 等正式审批字段，并保持 7 项待用户批准。

## 2. 计数与状态

```text
DECISIONS_TOTAL=7
DECISIONS_APPROVED=0
DECISIONS_PENDING=7
API_GAPS_TOTAL=10
API_GAPS_ACCOUNTED_FOR=10
API_GAPS_IMPLEMENTED_THIS_ROUND=0
API_GAPS_REMAINING=10
TEST_SCENARIOS_BEFORE=35
TEST_SCENARIOS_AFTER=57
TEST_FIXTURES_BEFORE=12
TEST_FIXTURES_AFTER=17
BUSINESS_POLICY_STATUS=PROPOSED
```

## 3. Gate

| Gate | 结果 | 说明 |
| --- | --- | --- |
| A Business decisions | `PARTIAL` | 7 项均待用户明确回复 |
| B Data model | `PARTIAL` | identity/scope/revocation 字段合同已修订，未落 schema |
| C API contract | `PARTIAL` | W01..W08/W10..W12 设计未实现；W09 内部复用、无新 route |
| D Authorization | `PARTIAL` | R3-B1 最低门禁已前置，产品 predicate 尚未实现 |
| E Test readiness | `PASS` | SC-01..SC-57、F01..F17 设计完成；本轮未执行产品测试 |
| F Implementation readiness | `PARTIAL` | B1 入口/退出条件明确，等待 7 项批准 |

## 4. 验收声明

本轮文档校验目标：

```text
DECISION_IDS=7_UNIQUE
API_GAP_IDS=10_UNIQUE
API_W_IDS=12_UNIQUE
DECISION_JSON_MARKDOWN_CONSISTENCY=PASS
API_GAP_TRACEABILITY=PASS
BATCH_DEPENDENCIES=PASS
TEST_SCENARIO_IDS=UNIQUE
SOURCE_FILE_REFERENCES=VALID
PRODUCT_TESTS=NOT_RUN_DOC_ONLY
```

上述是文档/静态验证口径，不是 R3-B1 产品测试通过。

## 5. 不变边界

```text
R3_PRODUCT_CODE_CHANGED=NO
R3_WIRELESS_UI_IMPLEMENTATION=NO
NEXT_DATABASE_SCHEMA_CHANGED=NO
NEXT_DATABASE_MIGRATION_CHANGED=NO
NEXT_AC_AP_MODEL_CHANGED=NO
R2_SITE_CLAIMS_CHANGED=NO
R2_DEVICE_MANAGEMENT_CHANGED=NO
REAL_DEVICE_ACCESS=NO
REAL_DEVICE_LOGIN=NO
NETWORK_SCAN=NO
REAL_TASK_EXECUTION=NO
PRODUCTION_DATA_CHANGED=NO
PUSH_STATUS=NOT_PUSHED
BRANCH_MERGE=NO
RELEASE_PUBLISH=NO
```

下一步：用户使用 `R3_B0_FINAL_USER_APPROVAL_SHEET.md` 回复 7 项选择；在收到明确回复前，不更新为 `APPROVED/FROZEN`，不启动 R3-B1。
