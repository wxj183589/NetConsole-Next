# R3 冻结政策适用范围

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
