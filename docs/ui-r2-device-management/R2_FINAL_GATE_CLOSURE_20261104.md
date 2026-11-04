# R2 最终 Gate 收口记录（2026-11-04）

## 最终裁决

`R2_FINAL_STATUS=PARTIAL`。

Site claims、设备管理功能和安全回归保持通过；唯一未关闭门禁是 Windows 当前有效 DPI 为 96，尚未取得原生 125% GUI 证据。没有因为环境阻断而修改 R2 权限合同，也没有开始 R3 产品实现。

## Gate 矩阵

| Gate | 结果 | 本轮证据 |
| --- | --- | --- |
| A Design | `PASS` | 沿用既有 P2B5 Site claims authority、稳定设备 ID、凭据引用和 FIT-AP 边界；未引入新架构 |
| B UI | `PASS` | 当前设备列表/新增/编辑/详情/分组/CSV/batch Browser E2E 通过；100% 回归截图已检查 |
| C Security | `PASS` | `tests/r2_site_claims_e2e.py` 实测 `31/31 PASS`；P2B5 scope、CSRF、审计、凭据脱敏、FIT-AP 限制保持通过 |
| D GUI | `PARTIAL` | 1920×1080 与 2560×1440 既有证据保持；本轮 Win32/Chrome DPI=96，原生 125% 未执行 |
| E Regression | `PASS` | 本轮 Site claims、P2B5、Vitest、vue-tsc、Vite、.NET builds、Service harness、Browser CRUD 均通过 |

## 本轮回归结果

| 检查 | 实际结果 |
| --- | --- |
| Site claims 既有场景 | `31/31 PASS` |
| P2B5 Site Scope HTTP | `PASS` |
| Device list/create/edit/detail | `PASS`（Browser CRUD 覆盖） |
| Multi-site authorization | `PASS`（claims E2E + P2B5） |
| Credential security | `PASS`（凭据不暴露/服务端引用） |
| FIT-AP creation restriction | `PASS` |
| Vitest | `3 files / 5 tests PASS` |
| vue-tsc | `PASS` |
| Vite build | `PASS`；仅有既有 chunk size warning |
| Service Release build | `PASS` |
| Tests Release build | `PASS`；前一次并行构建的 DLL 文件占用冲突已串行重跑消除 |
| Service harness | `PASS` |
| Browser CRUD E2E | `PASS` |
| `git diff --check` | `PASS` |

## 变更与边界

```text
NEXT_DATABASE_SCHEMA_CHANGED=NO
NEXT_DATABASE_MIGRATION_CHANGED=NO
NEXT_AC_AP_MODEL_CHANGED=NO
R3_PRODUCT_CODE_CHANGED=NO
REAL_DEVICE_ACCESS=NO
REAL_DEVICE_LOGIN=NO
NETWORK_SCAN=NO
REAL_TASK_EXECUTION=NO
PRODUCTION_DATA_CHANGED=NO
SMART_HUB_ORIGINAL_CHANGED=NO
WINDOWS_GLOBAL_SCALE_CHANGED=NO
USER_CHROME_TERMINATED=NO
```

本轮只新增/更新验收与审计文档，并在 Workspace 临时目录保留隔离 Browser/DPI 证据；未访问旧 NetConsole 或真实数据根。

