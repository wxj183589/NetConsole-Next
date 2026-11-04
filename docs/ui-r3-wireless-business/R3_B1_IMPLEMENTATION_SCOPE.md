# R3-B1 实施范围准备

基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
状态：`DESIGN_COMPLETE_AUTHORIZATION_PENDING`  
产品实现：`NOT_STARTED`

## 1. B1 目标

R3-B1 的第一批是受 Site claims 保护的只读业务读模型：

1. `AcDomain` list/detail：稳定 domain ID、成员 AC device IDs、拓扑 kind、证据和 revision；
2. `TypedApResource` list/detail：稳定候选/确认/冲突身份、serial、完整规范化 MAC 集合和来源；
3. `AcApObservation`：source AC/domain、source Site、captured time、raw manifest/provenance、identity basis 和 runtime observation；
4. 基础 Site authorization adapter：复用 R2 `P2B5SiteScope`，不创建第二套 claims store。

## 2. 计划接口（只设计）

| 接口 | 语义 | 必须门禁 |
| --- | --- | --- |
| `API-W01` | AC Domain list/detail | 当前 Site claims；只返回可见 domain/member |
| `API-W02` | AP Resource list/detail | 当前 Site claims；serial/MAC 是过滤条件，不是授权入口 |
| `API-W03` | AP Observation | observation-level Site predicate；不得泄露未授权 source AC |
| `API-W04` | pagination/count/filter | count 在过滤后计算；禁止未授权 count side-channel |
| `API-W05` | direct resource ID access | ID lookup 后仍执行同一 predicate；不存在与无权访问按安全错误合同处理 |
| `API-W06` | serial/MAC normalized filters | exact normalized match；不做 suffix/fuzzy/cross-Site match |
| `API-W09` | internal scope predicate | 只内部复用 `P2B5`，不新增 route |

所有接口仍为 `DESIGNED_NOT_IMPLEMENTED`。B1 不包含 AP Group write、无线 UI、真实 collection、Task execution 或 audit persistence。

## 3. 来源与字段约束

- AC 资产来源继续是已有 `device_inventory.id`；B1 不复制 AC inventory。
- AP identity 必须携带 serial、完整 normalized MAC set、identity status、evidence basis、conflict reason 和 source revision。
- Observation 必须携带 source AC ID、source Site claim/revision、captured time、raw manifest ID/hash（如可用）和 projection revision。
- 任何摘要只从授权 observation 子集投影；无 observation、冲突或 stale 不能被渲染成 confirmed/online。
- D04/D06/D07 的冻结规则分别控制 group scope、task target intersection 和 audit provenance；它们不因 B1 只读而失效。

## 4. 复用与依赖

B1 实现时优先复用现有 device inventory repository、H3C parser/facts、raw manifest/revision、R2 claims predicate、分页和统一错误模型。禁止在 UI 端拼接授权、通过 group/name 推断身份，或为无线建立旁路 repository。

前置依赖是冻结合同、单独的 product coding 授权、fixture/isolated test harness 和最终的 schema impact decision。若现有 raw/facts 无法在不写库的情况下提供稳定 projection，必须停在授权评审，不得自行改 schema。

## 5. 明确排除

- 不修改数据库 schema 或 migration；
- 不写入真实或开发真实数据；
- 不接入真实 AC/FIT-AP、不登录、不扫描、不执行任务；
- 不开发无线 UI；
- 不把 10 个 API gap 标记为已实现；
- 不改变 R2 Site claims、R2 device management 或 Windows 125% acceptance 状态。

## 6. B1 退出条件（未来实现后判断）

只有在独立授权后，且 `R3_B1_SECURITY_AND_TEST_GATE.md` 的未来 gates 全部通过、R2 回归无回退、文档与实现 revision 一致时，才可把产品实现状态从 `NOT_STARTED` 改为已实现。当前退出条件未执行。
