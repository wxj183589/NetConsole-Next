# R2 Site Claims Endpoint Audit

## 审计调用链

`HTTP session -> WebUserIdentity -> P2B5SiteScopeStore.Resolve -> DeviceSiteScopeGuard -> Catalog SQL/transaction scope check -> device audit`

P2B5 authority 仍位于 `src/NetConsole.Next.Service/P2B5SiteScope.cs`。设备实现位于 `Program.cs`、`DeviceSiteScope.cs`、`DeviceManagement.cs` 和 `DeviceBatchOperations.cs`。客户端 `web/src/api/client.ts` 新增 `getDeviceSites()`，`DevicesView.vue` 只据服务端返回的 Site 列表切换，不自行扩展 Site。

## 路由核对

| 路由族 | 旧缺口 | 本轮修复 | 负向边界 |
| --- | --- | --- | --- |
| `GET /api/system/device-sites` | 无授权 Site 聚合 API | 新增 scoped Site 查询 | USER_NONE 403；USER_A 只返回 A |
| `GET /api/system/devices` | 客户端 `siteId` 可筛选但无 claims SQL | 列表、summary、分页前加入 `d.site_id IN (...)` | B/C 不出现在结果或显式参数 403 |
| `GET /api/system/devices/{id}` | 直接按 ID 读取 | scoped inventory 命中后再读详情 | 其他 Site 404，不泄露存在性 |
| `GET /api/system/device-groups` | 直接按请求 Site 查询 | group Site SQL scope，计数限定同 Site | 跨 Site 参数 403 |
| group/device create | 仅 SUPERADMIN + 存在性 | 保留角色门禁，校验目标 Site；group/profile 引用同 scope | ADMIN 仍 403，越权 target 不写入 |
| device update | 未核对源设备 Site | 事务中核对源 Site 与目标 Site | 跨 Site/越权源不写入 |
| CSV preview/commit | 无 claims 检查 | preview 每行 + commit 事务再次检查 | 混合或越权行不能静默提交 |
| batch preview/status/confirm/cancel | 仅角色和版本/幂等 | 源目标、目标 Site、目标 group 逐目标检查 | 未授权 operation 不返回详情 |
| `GET /api/system/terminal/devices` | profile 列表未按来源 Site 收敛 | 通过 `collection_sources.site_id` 过滤 | 关联档案不跨 Site 展示 |

删除和 CSV 导出在当前代码中没有路由，记录为 `NOT_APPLICABLE`；本轮没有添加一个未经既有合同定义的新删除/导出接口。
