# R2 Final GUI and Contract Acceptance

## 最终裁决

`SITE_CLAIMS_CONTRACT=PASS`，`FINAL_STATUS=PARTIAL`。R2 设备管理的服务端 Site claims 合同已完成并通过隔离多用户回归；整体仍因当前机器没有真实有效 Windows 125% GUI 证据而保持 PARTIAL。

## 状态矩阵

| 项目 | 结果 |
| --- | --- |
| Workbench / 设备列表 / Site selector / 创建 / 编辑 / 详情 | PASS |
| Site claims、multi-site inventory/group/detail | PASS |
| CSV import、batch operations | PASS |
| 稳定设备 ID、凭据引用、FIT-AP 限制 | PASS |
| 1920×1080 GUI | PASS，视觉 fixture 无 console/page error |
| 2560×1440 GUI | PASS，视觉 fixture 无 console/page error |
| Windows 原生 125% | PENDING；有效 Win32/Chrome DPI 仍为 96 |
| 服务型 Browser CRUD E2E | PASS |
| R3 无线 UI | NO IMPLEMENTATION，仍 disabled |

## Gate

| Gate | 结果 | 说明 |
| --- | --- | --- |
| Gate A Design | PASS | 沿用 P2B5 authority；没有新权限架构或 SmartHub 资产 |
| Gate B UI | PASS | Site selector 是局部合同适配，既有 R2 布局未重做 |
| Gate C Security | PASS | 31/31 Site claims 专项、角色/CSRF/审计、负向和关联引用过滤通过 |
| Gate D GUI | PARTIAL | 1920/2560 PASS；原生 125% PENDING |
| Gate E Regression | PASS | 前端、.NET、harness、Browser CRUD、视觉复验均通过 |

## 不变更项

- `NEXT_DATABASE_SCHEMA_CHANGED=NO`
- `NEXT_DATABASE_MIGRATION_CHANGED=NO`
- `NEXT_DEVICE_IDENTITY_MODEL_CHANGED=NO`
- `NEXT_CREDENTIAL_STORAGE_MODEL_CHANGED=NO`
- `NEXT_AC_AP_MODEL_CHANGED=NO`
- `REAL_DEVICE_ACCESS=NO`、`REAL_DEVICE_LOGIN=NO`、`NETWORK_SCAN=NO`
- `PRODUCTION_DATA_CHANGED=NO`、`SMART_HUB_ORIGINAL_CHANGED=NO`
- `PUSH_STATUS=NOT_PUSHED`、`BRANCH_MERGE=NO`、`RELEASE_PUBLISH=NO`

## 收口条件

只剩在真实有效 Windows 125% 显示环境重新跑 GUI 门禁；本轮不修改全局缩放、不注销、不重启、不结束用户浏览器。R3 进入实现前仍需冻结无线 API 合同，详见 `R3_WIRELESS_UI_READONLY_PREPARATION.md`。

## 2026-11-04 当前运行复核

本轮实际 Win32/Chrome DPI 仍为 96（100%），隔离 headed Chrome 的 `visualViewport.scale=1`、`devicePixelRatio=1`、CSS viewport=`1584×849`，截图像素=`1584×849`。`LogPixels=120` 仅作为注册表值保留，未被当作原生 125% 证据。

当前裁决保持：`WINDOWS_NATIVE_125PCT=PENDING_ENVIRONMENT_NOT_READY`、`NATIVE_125PCT_SCREENSHOTS=0`、`GATE_D_GUI=PARTIAL`。本轮 Browser CRUD 只作为当前 100% 回归基线，未发现需要修复的 R2 UI 缺陷，也未修改 R2 产品代码。

详细记录见：`R2_WINDOWS_125PCT_NATIVE_FINAL_RECHECK.md`、`R2_GUI_NATIVE_DPI_EVIDENCE_MATRIX.md` 和 `R2_FINAL_GATE_CLOSURE_20261104.md`。
