# R3 无线业务准备度最终裁决

## 2026-11-06 当前切片指针

AP Group 来源身份与 schema24 最小 projection 修复已完成本机验收，状态 `LOCAL_ACCEPTANCE_PASS`，总体仍 `PARTIAL`。7 项业务决策已冻结，当前用户持续任务已授权推进；下方批准前/准备期的“未授权、代码未开始、schema未变”保留为历史，不要求重复授权。

当前 source-group 合同、SC-28/29/36..41 映射及 schema23/Linux、schema24/group 边界分别见 [AP Group 合同](R3_B0_AP_GROUP_CONTRACT.md)、[57 场景矩阵](R3_B0_TEST_SCENARIO_MATRIX.md) 和 [Schema 影响](R3_B1_SCHEMA_AND_MIGRATION_IMPACT.md)。本机 Group 合同10场景、projection、Runtime13/13、schema24、HTTP16 marker、Service harness、Chrome mocked15状态与前端检查证据见 [本机验收记录](R3_FULL_CHAIN_FINAL_ACCEPTANCE.md)；完整 identity/replacement、Domain/group 生命周期与真实设备验收不由该切片宣告完成。

## 历史准备度裁决与冻结更新


日期：2026-11-04

## 2026-11-05 当前冻结更新

当前权威冻结基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`。7/7 决策已冻结，API design 已完成但 10 个 gap 仍未实现；B1 为 `DESIGN_COMPLETE_AUTHORIZATION_PENDING`，产品代码、schema、migration、无线 UI 和真实设备验证均未开始。下方 2026-11-04 的 readiness 数值保留为批准前历史记录，不能解释为冻结前已批准。

## 最终结果

```text
R3_AC_INVENTORY_CONTRACT=IMPLEMENTED_PARTIAL
R3_AP_FACTS_CONTRACT=IMPLEMENTED_PARTIAL
R3_AC_AP_RELATION_CONTRACT=DESIGNED_NOT_IMPLEMENTED
R3_AP_GROUP_CONTRACT=IMPLEMENTED_PARTIAL
R3_SERIAL_MAC_IDENTITY_CONTRACT=EVIDENCE_REQUIRED
R3_RUNTIME_STATE_CONTRACT=DESIGNED_NOT_IMPLEMENTED
R3_MULTI_AC_TOPOLOGY_CONTRACT=DESIGNED_NOT_IMPLEMENTED
R3_SITE_CLAIMS_CONTRACT=IMPLEMENTED_PARTIAL
R3_TASK_API_CONTRACT=IMPLEMENTED_PARTIAL
R3_AUDIT_API_CONTRACT=IMPLEMENTED_PARTIAL

R3_API_GAPS=10
R3_USER_DECISIONS_PENDING=7
R3_FIRST_IMPLEMENTATION_BATCH=R3-B0

R3_API_CONTRACT_READINESS=PARTIAL
R3_DATA_MODEL_READINESS=PARTIAL
R3_SITE_AUTHORIZATION_READINESS=PARTIAL
R3_WIRELESS_UI_IMPLEMENTATION=NOT_STARTED
```

## 已有真实实现

- AC 可作为普通 `device_inventory` 设备类型管理，保留稳定 `device_inventory.id`、Site/group、厂商/型号/IP 和 H3C profile reference。
- H3C allowlist 已包含 AP、radio、mesh、LLDP、transceiver 等只读命令；parser 能产出 AP/地址/radio/mesh/LLDP records。
- `h3c_facts` 支持 profile/kind/identity/source/captured/current/history 和离线 replay；Service harness 已验证 parser/replay、Current/history 和失败保留。
- `BoundedCollectionScheduler`、task definition/run、Raw manifest、取消/超时/中断恢复和审计基础可作为平台组件。
- R2 Site claims authority 已通过 31/31 专项和 P2B5 HTTP regression，可作为 R3 的授权底座。

## 只有设计/研究描述的能力

- `AcDomain`、`AcMemberDevice`、`ApResource`、`Observation`、`AcApBinding` 只存在于 R0-R1 设计方向，当前生产源码没有对应无线 schema/API。
- AP 跨 AC 的 serial/MAC identity、缺字段/冲突/替换策略没有实现或真实字段证据。
- R/M、R/B、I 的 normalized state、freshness、confidence、Current/PreviousEffective/Recent10 没有实现。
- stack/cluster/1+1 topology、AP group resource、wireless task plan/item 和无线 audit subject 没有实现。
- SmartHub 只提供 clean-room UI 结构参考，不能证明 Next 无线后端或真实数据能力。

## R2 对 R3 的关系

R2 Site claims 可以直接复用其 authority 和 fail-closed 解析原则，但不能直接把 generic H3C profile list、facts 或 single-command task route 当作无线业务 API。R3 必须增加 AC/domain-to-Site adapter、AP observation visibility predicate、relation revision 和无线审计字段；否则存在跨 Site 泄露和错误 identity merge 风险。

## 是否可以进入 R3 实现

当前不能进入无线 UI 或 schema 实施阶段。可以进入的下一步是 `R3-B0` 合同确认：由用户批准 7 项决策，随后以隔离 SQLite/脱敏 fixture 实现并验证 API envelope、identity conflict、multi-AC state 和 Site negative tests。完成 B0/B1-B5 的合同和回归后，才具备无线只读 UI 的实现入口。

本轮严格保持：

```text
R3_PRODUCT_UI_CHANGED=NO
R3_DATABASE_SCHEMA_CHANGED=NO
R3_DATABASE_MIGRATION_CHANGED=NO
R3_AC_AP_MODEL_CHANGED=NO
REAL_AC_CONNECTED=NO
REAL_AP_CONNECTED=NO
NETWORK_SCAN=NO
REAL_DEVICE_COMMAND_EXECUTED=NO
REAL_TASK_EXECUTED=NO
```

## 证据索引

- 代码级矩阵：`R3_WIRELESS_BUSINESS_API_CONTRACT_AUDIT.md`
- 身份/关系：`R3_AC_AP_IDENTITY_AND_RELATION_MODEL.md`
- 多 AC 状态：`R3_MULTI_AC_RUNTIME_STATE_CONTRACT.md`
- Site claims：`R3_SITE_CLAIMS_AUTHORIZATION_BOUNDARY.md`
- 批次/缺口：`R3_API_GAP_AND_IMPLEMENTATION_PRIORITY.md`
- 既有 R0-R1 设计：`D:\study\NetConsole-Next-Workspace\architecture-replanning\R0-R1-20261101\04_DEVICE_AND_AC_DOMAIN_MODEL.md`、`06_TASK_ORCHESTRATION_RFC.md`、`10_NEXT_CODE_GAP_AUDIT.md`
- SmartHub 参考交接：`D:\study\h3cai-smarthub\ui-reconstruction\docs\SMARTHUB_RESEARCH_REFERENCE_HANDOFF_FINAL.md`、`SMARTHUB_TO_NETCONSOLE_NEXT_UI_GAP_MATRIX.md`

## R3-B0 决策合同补充（2026-11-04）

本节是本轮 B0 设计审查的追加裁决，不改写上面的历史审计记录。上一版将合同确认批次标记为 `R3-B0`；本轮已经完成 B0 文档决策包，因此第一个产品实现批次修正为 `R3-B1`。

```text
R3_B0_AUDIT_STATUS=PARTIAL
R3_B0_USER_DECISIONS_IDENTIFIED=7
R3_B0_USER_DECISIONS_ALREADY_APPROVED=0
R3_B0_USER_DECISIONS_PENDING=7
R3_B0_BUSINESS_DECISION_POLICY=PROPOSED
R3_B0_API_GAPS_ACCOUNTED_FOR=10
R3_B0_API_GAPS_REMAINING=10
R3_B0_TEST_SCENARIOS_DESIGNED=35
R3_B0_TEST_FIXTURES_REQUIRED=12
R3_B0_FIRST_IMPLEMENTATION_BATCH=R3-B1
R3_B0_PRODUCT_CODE_CHANGED=NO
R3_B0_SCHEMA_CHANGED=NO
R3_B0_MIGRATION_CHANGED=NO
R3_B0_REAL_DEVICE_ACCESS=NO
R3_B0_FINAL_STATUS=PARTIAL
```

本轮新增文档：

- `R3_B0_BUSINESS_DECISION_PACKAGE.md` / `.json`
- `R3_B0_AC_AP_IDENTITY_MODEL.md`
- `R3_B0_MULTI_AC_ASSOCIATION_AND_STATE_MODEL.md`
- `R3_B0_AP_GROUP_CONTRACT.md`
- `R3_B0_SITE_AUTHORIZATION_CONTRACT.md`
- `R3_B0_API_CONTRACT_SPEC.md`
- `R3_B0_API_GAP_TRACEABILITY_MATRIX.md`
- `R3_B0_TEST_SCENARIO_MATRIX.md`
- `R3_B0_BATCH_IMPLEMENTATION_PLAN.md`
- `R3_B0_FINAL_READINESS_REPORT.md`

7 项决策仍须用户逐项选择后，才可把 proposed contract 转为 approved/frozen，并进入 R3-B1 产品实现。R2 `PARTIAL`、Windows native 125% `PENDING` 和所有真实设备限制保持不变。

## R3-B0 Contract Amendment 2026-11-04

本次追加修订保持上方历史口径不变：

```text
R3_B0_DECISIONS_APPROVED=0
R3_B0_DECISIONS_PENDING=7
R3_B0_API_GAPS_IMPLEMENTED_THIS_ROUND=0
R3_B0_API_GAPS_REMAINING=10
R3_B0_TEST_SCENARIOS_AFTER=57
R3_B0_TEST_FIXTURES_AFTER=17
R3_B0_POLICY=PROPOSED
R3_B0_PRODUCT_CODE_CHANGED=NO
```

AP Group 现区分 Domain-owned 与 AC-owned scope；Task snapshot 只限制目标上限，执行/恢复/读取/下载/审计均需 current claims；R3-B1 从第一条无线 API 开始应用 P2B5，R3-B5 只负责高级跨 AC、partial visibility 和 no-leak 专项；API-W09 不再新增 claims 暴露 route。详见 `R3_B0_CONTRACT_AMENDMENT_20261104.md` 和 `R3_B0_AMENDED_READINESS_REPORT.md`。

