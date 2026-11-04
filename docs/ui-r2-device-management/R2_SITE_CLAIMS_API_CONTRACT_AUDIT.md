# R2 Site Claims API Contract Audit

## 结论

`SITE_CLAIMS_API_CONTRACT=PASS`。本轮已将设备管理接入既有 P2B5 claims authority；没有新增身份系统、数据库 Schema/Migration、角色或设备身份模型。

## 权威与调用链

`WebAuthStore.ValidateSession -> WebUserIdentity(UserId, Role) -> P2B5SiteScopeStore.Resolve -> DeviceSiteScopeGuard -> Catalog SQL/transaction check -> InsertDeviceAudit`

普通用户的 claims 来自 `web_user_site_memberships`，按有效期、来源和 revision 解析；无 authority、无有效 Site、过期或 conflict 均拒绝。SUPERADMIN 保持既有 `GLOBAL` 合同。客户端的 `siteId` 仅用于筛选或写入目标，服务端不信任它作为权限来源。

## 修复位置

- `Program.cs`：新增 `GET /api/system/device-sites`，设备库存/详情/分组/导入/批量路由统一进入 `DeviceSiteScopeGuard`；写入继续保留 SUPERADMIN、Origin/CSRF 和审计门禁。
- `DeviceSiteScope.cs`：集中处理 Site allow、SQL `IN` 过滤和 scope 拒绝异常。
- `DeviceManagement.cs`：列表 summary/分页前过滤，详情按 ID scoped，group 统计限定同 Site，创建/编辑核对目标和源 Site，CSV preview/commit 逐行/事务复核，关联终端档案按 `collection_sources.site_id` 过滤。
- `DeviceBatchOperations.cs`：预览、operation 读取、确认、取消和事务执行逐目标核对源 Site、目标 Site 和分组 Site。
- `Catalog.cs`：终端设备 profile 列表按来源 Site 过滤。
- `web/src/api/client.ts`、`web/src/views/DevicesView.vue`：Site selector 改为服务端授权 Site 数据驱动；未重做 R2 页面布局。

## API 状态

| API | 结果 |
| --- | --- |
| Site list、inventory list/detail、group list | PASS，服务端 SQL scope |
| create/edit/group create | PASS，既有 SUPERADMIN 角色 + Site target check |
| CSV preview/commit | PASS，逐行和 commit 事务复核 |
| batch preview/status/confirm/cancel | PASS，逐目标和确认时复核 |
| terminal device reference list | PASS，按 profile source Site 过滤 |
| device delete / CSV export | `NOT_APPLICABLE`，当前无路由 |

## 验收证据

- `tests/r2_site_claims_e2e.py`：`31/31 PASS`。
- `tests/device_management_browser_e2e.mjs`：真实隔离 Service CRUD、CSV、batch、FIT-AP 和 ADMIN 写入拒绝 PASS。
- 完整 Service harness、Vitest、vue-tsc、Vite 和 .NET Release build PASS。

## 剩余边界

R2 Site claims 合同已闭环。Gate D 仍受真实 Windows 原生 125% 环境缺失影响；R3 无线 UI 和无线业务 API 仍未实现。
