# R2 Site Claims Final Acceptance

## 裁决

`SITE_CLAIMS_CONTRACT=PASS`，`GATE_C_SECURITY=PASS`。设备管理范围内适用的库存、Site 聚合、分组、详情、创建、编辑、CSV 导入和批量 operation 均已接入服务端 Site claims 或既有角色拒绝边界；删除和 CSV 导出当前无路由，已明确记录为 `NOT_APPLICABLE`。

`R2_FINAL_STATUS=PARTIAL`，原因仅为当前机器有效 Windows 显示 DPI/Chrome DPI 为 96（100%），原生 Windows 125% 尚未取得证据；这不降级本轮 Site claims 合同结论。

## Gate

| Gate | 结果 | 证据 |
| --- | --- | --- |
| A Design | PASS | authority、角色兼容性、legacy 边界和 API 矩阵已冻结 |
| B UI | PASS | Site selector 为服务端数据驱动，既有设备管理布局未重做 |
| C Security | PASS | `31/31` claims 专项、Browser 负向、CSRF、审计和完整 harness 通过 |
| D GUI | PARTIAL | 1920×1080、2560×1440 PASS；原生 125% PENDING |
| E Regression | PASS | Vitest、vue-tsc、Vite、.NET build、Service harness、Browser CRUD、视觉复验通过 |

## 未变更与剩余事项

- `NEXT_DATABASE_SCHEMA_CHANGED=NO`，`NEXT_DATABASE_MIGRATION_CHANGED=NO`。
- `NEXT_AC_AP_MODEL_CHANGED=NO`，设备稳定 ID 和凭据安全边界保持。
- 无真实设备访问、真实登录、网络扫描、生产数据写入或 SmartHub 修改。
- `/wireless` 仍是 disabled；R3 仍缺授权 AC inventory、AP facts、AC/AP relation、AP groups、serial/MAC identity、runtime status、stack/cluster topology、task/audit 的正式无线业务合同。
- 原生 Windows 125% 需在真实有效 125% 环境重新执行 GUI 门禁；本轮不修改系统缩放。
