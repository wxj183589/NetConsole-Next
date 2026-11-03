# R2 Site Claims API Contract Audit

## 结论

`SITE_CLAIMS_API_CONTRACT=PARTIAL_CONTRACT_GAP`。

现有 P2B5 Site claims 解析和网络规划 API 的服务端保护仍有效，但设备管理库存 API 尚未接入同一 claims 过滤合同。本轮没有伪造 Site 列表、扩大身份模型、修改 schema/migration 或把 UI 的 `local` 作用域宣称为完整多 Site 授权。

## 已确认的身份与 claims 规则

- `WebUserIdentity` 以稳定 `UserId`、用户名、角色和会话标识服务端身份。
- `P2B5SiteScopeStore.Resolve` 对 `SUPERADMIN` 返回明确的 `GLOBAL`；普通用户从 `web_user_site_memberships` 读取有效 claims。
- claims 按来源和 revision 解析；更新的 `REVOKED` 不会被旧 `ACTIVE` 复活；冲突返回 `CONFLICT`，无有效 Site 返回 `DENIED`。
- P2B3 读接口通过 `P2B3ReadGuard` 绑定 Site scope，并拒绝请求未授权的 `siteId`。既有 P2B5 测试覆盖 active/expired/conflict claim。

## 设备管理路由审计

当前 `Program.cs` 的以下路由通过会话、回环来源和写入 CSRF/角色门禁，但没有调用 `P2B5SiteScopeStore`：

- `GET /api/system/devices`
- `GET /api/system/devices/{id}`
- `GET/POST /api/system/device-groups`
- `POST /api/system/devices`
- `PUT /api/system/devices/{id}`
- 设备 CSV preview/commit 和设备批量操作

它们可以按 `siteId`、`groupId` 查询或写入，并由 Catalog 校验 Site/分组是否存在；这不是“当前用户 claims 允许该 Site”的等价校验。设备写入当前主要是 `SUPERADMIN` 门禁，ADMIN 被拒绝，但这仍不能替代普通用户的多 Site claims 合同。

## 分层结果

| 合同项 | 结果 | 说明 |
| --- | --- | --- |
| `SITE_UI_SCOPE_SUPPORTED` | PASS（单当前 Site UI） | R2 显示 `siteId`、分组和当前作用域，不伪造 Site 树 |
| `SITE_LIST_API_AVAILABLE` | NO | 未发现供设备管理 UI 使用的“当前用户全部授权 Site” API |
| `MULTI_SITE_CLAIMS_CONTRACT_VERIFIED` | PARTIAL | P2B3 网络规划链路有合同，设备库存链路没有统一接入 |
| `SERVER_SIDE_SITE_AUTHORIZATION_VERIFIED` | PARTIAL | claims 服务端解析真实存在，但未覆盖设备库存/分组/CRUD 全部路由 |
| `SITE_SCOPE_REGRESSION` | PARTIAL_CONTRACT_GAP | R2 UI 不越权伪造，但完整多 Site 设备授权待独立后端任务 |

## 本轮决策

不在 R2 末轮添加 Site 列表/claims API，不修改用户身份模型或数据库。后续独立契约应至少提供：当前用户授权 Site 列表、默认 Site、claims revision/有效期、Site 切换、设备列表/分组/详情/创建/编辑的同一服务端 scope decision，以及终端入口的设备所属 Site 校验。
