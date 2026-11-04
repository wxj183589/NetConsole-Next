# NetConsole-Next R3 无线业务冻结基线

冻结版本：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
冻结日期：`2026-11-05`  
项目：`NetConsole-Next`  
状态：`FROZEN`

## 1. 用户批准记录

用户在 2026-11-05 明确批准：

> 同意按照修订后的 OPTION_A，正式批准并冻结 R3 的全部 7 项业务决策。

本文件是冻结后的当前权威基线。`R3_B0_CONTRACT_AMENDMENT_20261104.md`、`R3_B0_FINAL_USER_APPROVAL_SHEET.md` 以及 2026-11-04 的 readiness 报告保留为批准前历史证据，不回写为批准前已冻结。

## 2. 七项冻结决策

| ID | 冻结选择 | 修订 | 冻结规则摘要 |
| --- | --- | --- | --- |
| `R3_DECISION_01` | `OPTION_A` | `20261104-amended` | 显式稳定 `AcDomain.id`，引用已有 AC device ID；拓扑 kind 只有成员/HA/IRF/cluster 证据才确认。 |
| `R3_DECISION_02` | `OPTION_A` | `20261104-amended` | serial 与完整规范化 MAC 集合是 exact evidence；缺失、候选、冲突、未知不猜测合并。 |
| `R3_DECISION_03` | `OPTION_A` | `20261104-amended` | 返回授权 observation 子集；AP 摘要只从可见 observation 投影，标识 `PARTIAL_SCOPE`。 |
| `R3_DECISION_04` | `OPTION_A` | `20261104-amended` | AP Group 为 AC/domain 侧只读观察；只有明确 domain-shared evidence 才可 Domain-owned，否则 AC-owned；不提供平台写入。 |
| `R3_DECISION_05` | `OPTION_A` | `20261104-amended` | 保留 raw state，规范化为 `RUNNING_MASTER/RUNNING_BACKUP/OFFLINE/UNKNOWN/STALE/CONFLICT`；采集失败不等于 offline。 |
| `R3_DECISION_06` | `OPTION_A` | `20261104-amended` | `EXECUTION_TARGETS = CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS`；执行、恢复、读取、下载、报告、审计均重检当前 claims。 |
| `R3_DECISION_07` | `OPTION_A` | `20261104-amended` | 审计只记录 actor/action/subject/revision/reason 与 Raw Manifest ID/hash/source revision，不记录凭据、命令原文或敏感输出。 |

D04 和 D06 的 `OPTION_A` 均采用 2026-11-04 amendment；D04 的修改点是 AP Group 的 Domain-owned 需证据，D06 的修改点是当前 claims 交集和撤销后的受保护边界。冻结后的机器可读选择是 `selectedOption=OPTION_A`，不要把 `OPTION_A_REQUIRES_REVISION` 当作当前选择。

## 3. 状态分离

冻结业务决策不等于产品实现完成：

| 层次 | 当前状态 |
| --- | --- |
| Business decision | `FROZEN` |
| AC/AP/domain/group/state/task/audit contract | `FROZEN` |
| API design | `DESIGNED` |
| API implementation | `NOT_IMPLEMENTED` |
| R3 wireless UI | `NOT_STARTED` |
| Database schema change | `NO_CHANGE_THIS_ROUND` |
| Database migration | `NOT_STARTED` |
| R3-B1 product coding authorization | `PENDING_SEPARATE_AUTHORIZATION` |

## 4. API、Site claims 与任务边界

- R3 API gap 总数为 `10`，全部已进入合同、优先级、测试和 B1 计划追踪；本轮关闭数为 `0`，剩余数为 `10`。
- R3-B1 第一批只准备 AC Domain、Typed AP Resource、AC/AP Observation 和基础 Site authorization 的只读接口设计。
- 第一条无线 route 起就必须使用 Site claims predicate；`API-W09` 仍是 `INTERNAL_P2B5_SCOPE_PREDICATE_NO_NEW_ROUTE`，不新建 claims 暴露 route。
- AP 资源、observation、状态、group、计数和审计均不得通过全局摘要或 generic Task API 绕过授权。
- D06 规定的 Task scope 只形成最大目标边界，当前 claims 决定实际可执行和可读取集合；不允许新 claims 扩张既有 task。

## 5. 测试与证据边界

当前已定义 `57` 个测试场景和 `17` 个最小 fixture。它们是 B1 的离线测试设计，不是本轮执行结果；未来 gates 均保持 `NOT_RUN_DOC_ONLY`，不得填写为 `PASS`。

本轮未访问真实 AC/FIT-AP，未登录真实设备，未扫描网络，未执行真实任务，未触碰 Production 或 `D:\NetConsoleData-dev`，未修改 SmartHub 原始研究资料，未改变 Windows 全局缩放，也未终止用户 Chrome。

## 6. 冻结后的变更控制

任何改变上述选择、Site authority、AP identity、任务撤销语义或审计 provenance 的变更，必须新增 decision revision 并重新获得用户批准。数据库 schema、migration、无线 UI、真实设备采集和任务执行均不因本文件自动获得授权。

## 7. 机器可读状态

```text
R3_BUSINESS_POLICY_STATUS=FROZEN
R3_FROZEN_BASELINE=NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1
USER_DECISIONS_TOTAL=7
USER_DECISIONS_APPROVED=7
USER_DECISIONS_PENDING=0
API_GAPS_TOTAL=10
API_GAPS_ACCOUNTED_FOR=10
API_GAPS_IMPLEMENTED_THIS_ROUND=0
API_GAPS_REMAINING=10
TEST_SCENARIOS=57
TEST_FIXTURES=17
R3_B1_PRODUCT_IMPLEMENTATION=NOT_STARTED
R3_PRODUCT_CODE_CHANGED=NO
R3_WIRELESS_UI_IMPLEMENTATION=NO
R3_B1_DATABASE_CHANGE_AUTHORIZED=NO
PUSH_STATUS=NOT_PUSHED
```
