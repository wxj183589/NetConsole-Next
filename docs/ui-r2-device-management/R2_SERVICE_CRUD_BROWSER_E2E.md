# R2 Service-backed CRUD Browser E2E

## 结论

`SERVICE_CRUD_BROWSER_E2E=PASS`。2026-11-04 使用现有 `tests/device_management_browser_e2e.mjs`，由隔离 .NET Service 提供真实 HTTP、Cookie/CSRF、SQLite Catalog 和构建后的 Vue UI。

## 隔离边界

- Service：`dotnet .../NetConsole.Next.Service.dll`，绑定 `http://127.0.0.1:18773`。
- 数据根：`D:\study\NetConsole-Next-Workspace\test-data\r2-service-crud-e2e-20261104-final`，仅为本次运行新建的单一测试根；服务停止后已按精确路径退役。
- 环境：`NETCONSOLE_NEXT_TERMINAL_ENABLED=0`、`NETCONSOLE_NEXT_H3C_ENABLED=0`、Development；没有绕过 `Global\NetConsoleNext.Service` 单实例保护，也没有终止用户进程。
- 浏览器：Playwright + 本机 Chrome；E2E 视口为测试脚本显式的 `1600×1000`、`deviceScaleFactor=1`，这不是原生 125% 证据。
- 测试数据：`superadmin/admin` 初始化测试账号、`local` 测试 Site、虚构 RFC 5737 地址；不读取 Production/Dev 数据根，不连接设备。

## 覆盖场景

| 场景 | 结果 |
| --- | --- |
| 普通设备创建、结果回显、Site/分组关联 | PASS |
| 设备列表、详情、稳定 ID、稳定 IP 编辑 | PASS |
| 详情审计记录与凭据状态展示 | PASS |
| 分组创建、分组分配、作用域按钮切换 | PASS |
| 厂商和设备类型服务端筛选 | PASS |
| 批量禁用预览、刷新恢复、原子确认 | PASS |
| CSV 正常预览/提交与重复地址冲突 | PASS |
| 直接 FIT-AP 新建 API 拒绝 | PASS，HTTP 400，业务错误明确说明 FIT-AP 必须由 AC 管理 |
| CSV 新建 FIT-AP 预览拒绝 | PASS，预览行含 `FIT_AP_MUST_BE_MANAGED_BY_AC`，不可提交 |
| ADMIN 写入拒绝 | PASS，HTTP 403 |

## 本轮测试合同修正

第一次执行暴露出 E2E 仍查找已移除的“设备分组下拉框”。当前 R2 UI 的真实入口是左侧 `button.scope-item`，脚本已按现有 UI 对齐；这属于测试选择器漂移，不是业务逻辑放宽。随后还修正了 E2E 通过现有 `/api/auth/csrf` 获取 CSRF token，避免把通用 CSRF 403 误判为 FIT-AP 业务拒绝。

## 浏览器证据

保留于 `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\service-crud-e2e`：

- `device-created.png`
- `device-detail-audit.png`
- `device-batch-recovered-and-committed.png`
- `device-imported.png`

这些是虚构隔离数据的 UI 证据，不是生产或真实设备截图。
