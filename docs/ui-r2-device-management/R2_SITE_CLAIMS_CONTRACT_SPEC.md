# R2 Site Claims 服务端合同

## 结论

本轮将 R2 设备管理接入既有 P2B5 Site claims 权威，未新增身份、角色、Schema 或 Migration。服务端以已认证 `WebUserIdentity.UserId` 解析 `P2B5SiteScopeStore`，客户端 `siteId` 只能作为筛选/目标参数，不能作为授权来源。

## 权威与决策

- 权威表：`web_user_site_memberships`，按 `user_id`、`source_kind`、`source_reference` 和最高 `revision` 解析。
- `SUPERADMIN`：沿用既有合同，返回 `GLOBAL`，可访问现有 Catalog 全部 Site。
- 普通用户：只允许有效 `ACTIVE` claims 的 Site 集合；过期、`REVOKED`、`CONFLICT`、无 claims 或 authority 不可读时 fail-closed。
- 设备路由统一调用 `DeviceSiteScopeGuard`；`DENIED`/`CONFLICT`/`UNAVAILABLE` 映射为既有 `P2B_SITE_SCOPE_*` 403。
- 未授权设备详情按过滤后不存在处理为 404；未授权 Site 参数直接 403；列表和 summary 先做 SQL scope 过滤，再分页和计数。

## API 合同

| API | 读/写 | Site claims | 结果 |
| --- | --- | --- | --- |
| `GET /api/system/device-sites` | 读 | 返回 `ALL_AUTHORIZED_SITES`；SUPERADMIN 返回全局 Site | 已实现 |
| `GET /api/system/devices` | 读 | Site、group、search、vendor、type、status、分页和 summary 均在服务端 scope 后执行 | 已实现 |
| `GET /api/system/devices/{id}` | 读 | 稳定 ID 仅能命中当前 scope；关联终端档案按来源 Site 再过滤 | 已实现 |
| `GET /api/system/device-groups` | 读 | Site 空值表示全部授权 Site，显式未授权 Site 403 | 已实现 |
| `POST /api/system/device-groups` | 写 | 保留 SUPERADMIN 角色合同，并校验目标 Site | 已实现 |
| `POST /api/system/devices` | 写 | 保留 SUPERADMIN 角色合同，并校验目标 Site/分组/终端档案 | 已实现 |
| `PUT /api/system/devices/{id}` | 写 | 校验源设备 Site、目标 Site、目标分组和终端档案 | 已实现 |
| `POST /api/system/devices/import/preview` | 写前预览 | 每行 Site、既有 ID、终端档案都做 scope 检查 | 已实现 |
| `POST /api/system/devices/import/commit` | 写 | 事务内再次校验每个 candidate 的目标 Site、既有设备源 Site 和引用 | 已实现 |
| `POST /api/system/devices/batch`、operation 查询/confirm/cancel | 写/状态 | 逐目标校验源 Site、目标 Site 和目标分组；确认时再次校验 | 已实现 |
| 设备删除 API | — | 当前仓库无适用路由 | `NOT_APPLICABLE`，未虚构接口 |
| 设备 CSV 导出 API | — | 当前仓库无适用路由 | `NOT_APPLICABLE`，未虚构接口 |

写入仍遵守现有角色、Origin/CSRF、审计、幂等、版本冲突、FIT-AP AC 管理约束。普通 `ADMIN` 保持既有写入 403 合同；Site claims 不会扩大其角色能力。

## 兼容性边界

没有把历史空 Site、未分组设备或缺失 claims 自动归入用户 Site；没有修改设备稳定 ID、凭据存储、DPAPI、AC/FIT-AP 模型。R3 无线页面仍未实现。
