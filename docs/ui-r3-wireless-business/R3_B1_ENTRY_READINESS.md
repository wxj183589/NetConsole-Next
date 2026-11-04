# R3-B1 入口准备度

基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
入口结论：`PARTIAL`  
入口状态：`DESIGN_COMPLETE_AUTHORIZATION_PENDING`

## 1. 当前裁决

七项业务决策已冻结，AC/AP、multi-AC、AP Group、runtime state、Site claims、Task scope 和 audit provenance 合同已同步，10 个 API gap 已可追踪，57 个场景和 17 个 fixture 已定义。由此达到 B1 的文档/设计入口准备度；尚未达到产品编码、schema/migration 或无线 UI 的执行授权。

## 2. 入口状态键

```text
R3_BUSINESS_POLICY_STATUS=FROZEN
R3_FROZEN_BASELINE=NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1
USER_DECISIONS_TOTAL=7
USER_DECISIONS_APPROVED=7
USER_DECISIONS_PENDING=0
AC_DOMAIN_CONTRACT=FROZEN
AP_IDENTITY_CONTRACT=FROZEN
MULTI_AC_VISIBILITY_CONTRACT=FROZEN
AP_GROUP_CONTRACT=FROZEN
RUNTIME_STATE_CONTRACT=FROZEN
TASK_SCOPE_CONTRACT=FROZEN
AUDIT_PROVENANCE_CONTRACT=FROZEN
API_GAPS_TOTAL=10
API_GAPS_ACCOUNTED_FOR=10
API_GAPS_IMPLEMENTED_THIS_ROUND=0
API_GAPS_REMAINING=10
TEST_SCENARIOS=57
TEST_FIXTURES=17
R3_B1_DATABASE_CHANGE_REQUIRED=UNDETERMINED_FOR_FUTURE_IMPLEMENTATION
R3_B1_DATABASE_CHANGE_AUTHORIZED=NO
R3_B1_PRODUCT_IMPLEMENTATION=NOT_STARTED
R3_PRODUCT_CODE_CHANGED=NO
R3_WIRELESS_UI_IMPLEMENTATION=NO
NEXT_DATABASE_SCHEMA_CHANGED=NO
NEXT_DATABASE_MIGRATION_CHANGED=NO
R2_SITE_CLAIMS_CHANGED=NO
R2_DEVICE_MANAGEMENT_CHANGED=NO
R2_WINDOWS_NATIVE_125PCT=PENDING
R2_FINAL_STATUS=PARTIAL
REAL_DEVICE_ACCESS=NO
REAL_DEVICE_LOGIN=NO
NETWORK_SCAN=NO
REAL_TASK_EXECUTION=NO
PRODUCTION_DATA_CHANGED=NO
PUSH_STATUS=NOT_PUSHED
BRANCH=codex-A/netconsole-next-r2-device-management-ui
```

## 3. B1 初始模块与接口

准备进入的模块仅限：AC Domain read projection、Typed AP Resource identity projection、AC/AP Observation projection、claims adapter、pagination/filter/count/direct-ID authorization tests。首批接口为 W01/W02/W03 及其受保护的 W04/W05/W06；W09 仍是内部 predicate。

不进入 B1：无线 UI、AP Group write、Task execution、真实 collection、persistent wireless schema、migration、跨 AC 复杂聚合和任何 R2 claims 改造。

## 4. 需要额外批准的事项

进入产品编码前需要明确的 implementation authorization；选择正式 persistence 时还需要 schema/migration authorization；真实设备、真实任务、无线 UI 和发布各自独立。当前推荐 B1 先采用非持久 read-only projection，但这个推荐不替代未来 schema 决策。

## 5. 对验收问题的直接回答

1. 七项决策：是，7/7 已正式冻结。
2. 冻结文档版本：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。
3. D04：是，采用修订后的 `OPTION_A`，Domain-owned 需要明确 domain-shared evidence，否则 AC-owned。
4. D06：是，采用修订后的 `OPTION_A`，有效目标是创建快照与当前授权目标的交集，并在受保护边界重检 claims。
5. API-W09：是，`INTERNAL_P2B5_SCOPE_PREDICATE_NO_NEW_ROUTE`。
6. B1 首批：AC Domain、Typed AP Resource、AC/AP Observation 和基础 Site authorization，第一条 route 即受 claims 保护。
7. 10 个 gap：是，全部有合同/接口/安全/测试/批次映射；关闭 0，剩余 10。
8. B1 模块：见本节第 3 节，均为设计范围。
9. DB 修改：本轮没有；未来正式 persistence 仍为 `UNDETERMINED`，未授权。
10. Migration：本轮没有，也未授权。
11. 推荐持久化：B1 首批优先非持久只读 projection；稳定身份/历史/Task/audit 的持久化另行决策。
12. 额外授权：需要产品编码授权；schema/migration、UI、真实设备/任务另行授权。
13. 57/17：已完成文档定义与追踪，未执行，不能宣称测试通过。
14. B1 ready：文档/设计入口 `PARTIAL`，实现入口为授权待定，不是产品实现完成。
15. 产品代码：没有修改。
16. 提交/工作树：以最终交接中的本地 commit 和 status 为准；不 push、不 merge、不 release。

## 6. 未来通过标准

只有 `R3_B1_SECURITY_AND_TEST_GATE.md` 的全部 gates 从 `NOT_RUN_DOC_ONLY` 变为实际 `PASS`，并完成独立授权、实现、R2 regression 和数据影响审计，才能把本文件入口结论更新为 `PASS`。
