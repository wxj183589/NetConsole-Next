# NetConsole-Next R2 Final Acceptance

## 结论

R2 设备管理实现与 Site claims 服务端合同在本地隔离环境通过；`GATE_C_SECURITY=PASS`。整体 `FINAL_STATUS=PARTIAL`，唯一仍未关闭的正式门禁是当前机器有效 Windows 原生 125% GUI 证据不足。

## Gate

| Gate | 结果 | 依据 |
| --- | --- | --- |
| Gate A Design | PASS | 权威来源、scope decision、API 矩阵和兼容性边界明确 |
| Gate B UI | PASS | 设备列表、Site selector、创建、编辑、详情、分组、CSV、batch 回归通过 |
| Gate C Security | PASS | Site claims `31/31`，未授权 Site 无读写，角色/CSRF/审计保持 |
| Gate D GUI | PARTIAL | 1920×1080、2560×1440 PASS；Windows native 125% PENDING |
| Gate E Regression | PASS | Vitest 5/5、vue-tsc、Vite、.NET build、Service harness、Browser E2E PASS |

## 合同结果

- `SITE_CLAIMS_AUTHORITY_SOURCE=P2B5SiteScopeStore -> web_user_site_memberships`
- `MULTI_SITE_AUTHORIZATION=PASS`，USER_AB 只得到 A+B，聚合不是全库。
- `CROSS_SITE_ACCESS_DENIAL=PASS`，跨 Site 参数 403，未授权详情 404，无搜索/计数泄露。
- `DEVICE_IDENTITY_REGRESSION=PASS`，编辑管理 IP 不改变稳定 ID。
- `CREDENTIAL_SECURITY_REGRESSION=PASS`，仍只返回服务端 credential reference/configured 状态。
- `FIT_AP_CREATION_RESTRICTION=PASS`，直接创建和 CSV 新建均保留 AC 管理约束。
- `DEVICE_DELETE_AUTHORIZATION=NOT_APPLICABLE`、`CSV_EXPORT_AUTHORIZATION=NOT_APPLICABLE`，当前无对应路由。

## 安全边界

没有修改 Next Schema/Migration、身份模型、角色模型、AC/AP 模型、DPAPI/凭据存储、生产数据、真实设备或 SmartHub。R3 无线 UI 保持 `NO`；无线 API 合同缺口仍见 `R3_WIRELESS_UI_READONLY_PREPARATION.md`。

## Git/发布

本轮只做本地提交，不 push、不 merge、不发布 release。最终 commit、工作树和 DevStatus 交接以最终执行记录为准。
