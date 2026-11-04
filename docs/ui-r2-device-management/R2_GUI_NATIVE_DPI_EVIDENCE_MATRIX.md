# R2 GUI 原生 DPI 证据矩阵

日期：2026-11-04

## 证据矩阵

| SCREENSHOT_ID | PAGE | ROUTE | STATE | DISPLAY_SCALE | DISPLAY_DPI | CHROME_ZOOM | DPR | CSS_VIEWPORT | SCREENSHOT_PIXEL_SIZE | FONT_ENVIRONMENT | RESULT |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `DPI-PROBE-20261104` | 隔离 Chrome 环境探针 | `about:blank` | headed diagnostic | 100% | 96 | 100% / scale 1 | 1 | 1584×849 | 1584×849 | Segoe UI, Arial, 16px | `PENDING` |
| `R2-CURRENT-100-CREATED` | 设备管理/新增设备 | `/devices` | 新增表单 | 100% | 96 | 100% | 1（回归夹具） | 1600×1000 viewport | 1600×1000 viewport capture | 当前 Browser E2E 环境 | `PASS` |
| `R2-CURRENT-100-DETAIL` | 设备详情与审计 | `/devices` | 详情/更新通知 | 100% | 96 | 100% | 1（回归夹具） | 1600×1000 viewport | 1600×1000 viewport capture | 当前 Browser E2E 环境 | `PASS` |
| `R2-CURRENT-100-BATCH` | 批量操作预览 | `/devices` | batch recovered/committed | 100% | 96 | 100% | 1（回归夹具） | 1600×1000 viewport | 1600×1000 viewport capture | 当前 Browser E2E 环境 | `PASS` |
| `R2-CURRENT-100-IMPORT` | CSV 导入预览 | `/devices` | import preview/confirm | 100% | 96 | 100% | 1（回归夹具） | 1600×1000 viewport | 1600×1000 viewport capture | 当前 Browser E2E 环境 | `PASS` |
| `R2-NATIVE-125-*` | 设备管理各页面 | `/devices` | 原生 125% | 未取得 | 未取得 | 未取得 | 未取得 | 未取得 | 未生成 | 未验收 | `PENDING` |

## 证据位置

- 原生 DPI JSON/截图：`D:\study\NetConsole-Next-Workspace\temp\r2-native-125-recheck-20261104\`
- 当前 100% Browser E2E 截图：`D:\study\NetConsole-Next-Workspace\temp\r2-native-125-recheck-20261104\current-100pct-browser-evidence\`
- 当前 Browser E2E 脚本：`tests/device_management_browser_e2e.mjs:1-132`
- 原生 125% 阻断说明：`R2_WINDOWS_125PCT_NATIVE_FINAL_RECHECK.md`

## 解释规则

1. `DPI-PROBE-20261104` 是本机当前环境证据，不是 R2 页面验收截图。
2. `R2-CURRENT-100-*` 使用隔离 Browser E2E 的固定 1600×1000 viewport，只证明当前 100% 回归链路；不等同物理显示器原生 125%。
3. `R2-NATIVE-125-*` 在有效 Windows DPI 仍为 96 时保持 `PENDING`，不得用浏览器模拟或注册表值补齐。

