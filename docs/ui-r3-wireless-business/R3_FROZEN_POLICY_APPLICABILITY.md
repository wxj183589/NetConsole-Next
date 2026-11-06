# R3 冻结政策适用范围

## 2026-11-06 当前适用补充

本次 AP Group 来源身份实现已纳入用户持续开发任务；旧准备期的“授权待定、代码/UI未开始、schema未变”是历史记录，不要求重复授权。当前切片状态为 `LOCAL_ACCEPTANCE_PASS`，冻结合同仍不是测试通过证明。

当前边界：typed source-group evidence；名称 only 为 `AC / CANDIDATE / null key`；明确 shared evidence 与确认 Domain/成员集合一致才能 DOMAIN；membershipRevision 不随改名/不变刷新变化。schema23 已用于 Linux，schema24 仅保存可重建 group projection，Raw/Facts 仍为事实 Authority。Site claims 继续是唯一授权来源；source/Site 过滤先于 identity、members、count 和 revision，客户端不能指定可信 scope。

完整 AP 身份/replacement 与 Domain/group 生命周期、设备配置写回、真实数据复制和发布均未由本切片宣告完成。本机 10 个 Group 合同、projection、Runtime 13/13、schema24、HTTP 离线 fixture 16 marker、Service harness、Chrome API mocked 15 状态与前端检查均已有通过证据，范围见 [本机验收记录](R3_FULL_CHAIN_FINAL_ACCEPTANCE.md)；真实设备证据不足时仍为 `REAL_DEVICE_PENDING`。具体合同与验证映射见 [AP Group 合同](R3_B0_AP_GROUP_CONTRACT.md)、[测试矩阵](R3_B0_TEST_SCENARIO_MATRIX.md) 和 [schema24 边界](R3_B1_SCHEMA_AND_MIGRATION_IMPACT.md)。

## 历史冻结适用范围


基线：`NETCONSOLE-NEXT-R3-WIRELESS-BASELINE-20261105-R1`  
状态：`FROZEN`

## 当前适用

本冻结基线适用于 R3 无线业务的 AC Domain、AP identity、AC/AP observation、multi-AC visibility、AP Group、runtime state、Task/Report scope 和 audit provenance。它是业务合同与实施前置条件，不是产品实现证明。

R2 的 Site claims 仍是唯一授权来源。R3 不创建第二套无线权限表，不把页面导航、AP 名称、serial、MAC、group name 或 task profile role 当作授权依据。

## 第一条无线 API 的门禁

R3-B1 设计的 AC Domain List/Detail、AP Resource List/Detail、AP Observation、分页、count、serial/MAC filter 和 direct resource ID access 均必须在服务端执行当前 Site claims predicate。无授权、跨 Site、identity conflict、unknown state 和 count side-channel 均按 fail-closed 合同处理。

`API-W09` 只作为内部复用的 `P2B5` scope predicate，不新增“返回用户 claims”或“绕过 Site predicate”的 route。Generic Task API、全局 AP 摘要和客户端传入的 group scope 都不能旁路该门禁。

## 不能由冻结基线推导出的授权

本基线不授权：

- 修改产品代码、数据库 schema 或 migration；
- 开发无线 UI、真实 AC/FIT-AP 登录、网络扫描或真实采集；
- 执行、恢复、下载或报告真实任务；
- 写入 AP Group、改变现有 R2 Site claims 或增加通用权限；
- 复制、迁移、覆盖 Production 或真实开发数据。

B1 product coding、formal wireless persistence、schema/migration、真实设备验证和 UI 均需要各自的明确实施授权。当前 B1 readiness 是 `PARTIAL / DESIGN_COMPLETE_AUTHORIZATION_PENDING`。

## 证据与历史边界

57 个测试场景和 17 个 fixture 已定义但本轮未执行。2026-11-04 的 amendment、approval sheet 和 readiness reports 保持批准前事实；冻结后的当前状态以 `R3_WIRELESS_BUSINESS_DECISIONS_FROZEN_20261105.*` 为准。

```text
BUSINESS_DECISION=FROZEN
API_DESIGN=DESIGNED
API_IMPLEMENTATION=NOT_IMPLEMENTED
DATABASE_SCHEMA=NO_CHANGE_THIS_ROUND
DATABASE_MIGRATION=NOT_STARTED
DATABASE_CHANGE_AUTHORIZED=NO
R3_UI_IMPLEMENTATION=NOT_STARTED
```
