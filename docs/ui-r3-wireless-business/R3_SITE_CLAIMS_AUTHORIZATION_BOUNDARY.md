# R3 无线业务 Site claims 授权边界

日期：2026-11-04

## 1. R2 已确认的 authority

R2 的 Site authority 是 `web_user_site_memberships`，由 `P2B5SiteScopeStore.Resolve(CurrentIdentity)` 按 source + revision + validity + claim state 解析；冲突、过期、缺 authority 时 fail-closed，SUPERADMIN 才是 global scope。实现证据：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`。

设备 inventory/group/CRUD 使用 `DeviceSiteScope.BuildSqlFilter/Require`（`DeviceSiteScope.cs:8-35`）；H3C facts GET 在 `Program.cs:918-928` 做 scope resolution 与 `H3cProfileIsInScope`。本轮 `tests/r2_site_claims_e2e.py` 实测 `31/31 PASS`，P2B5 HTTP scope 也 PASS。

这些结论可以被 R3 复用，但不等于无线 API 已经存在。

## 2. 现有接口的边界

| 接口 | 当前授权 | 是否可直接作为 R3 无线接口 |
| --- | --- | --- |
| 设备 inventory/group/CRUD | Site claims + role/CSRF/审计 | 只能作为 AC 普通库存基础 |
| `GET /api/system/h3c/profiles` | 在 H3C feature 开启时返回 profile list，当前 route 未显示 wireless Site predicate | 否；必须先加 scoped adapter 或禁止复用 |
| `GET /api/system/h3c/profiles/{id}/facts` | P2B5 resolve + profile-in-scope | 可作为受限事实底层来源，不能直接当 AP API |
| `POST /api/system/h3c/profiles/{id}/offline-replay` | SUPERADMIN + bounded allowlist fixture | 仅测试/离线证据，不是生产无线刷新接口 |
| `POST /api/system/h3c/task-definitions` / `/runs` | SUPERADMIN；单 profile/单 command | 不能替代 Site-scoped wireless plan |
| `GET /api/system/tasks` | 通用任务历史 | 需要 R3 business subject/claims 过滤后才能用于无线视图 |

## 3. R3 目标授权函数

建议把无线每次读取/任务/审计分解为：

```text
allowed(user, ac_domain, ap_resource, observation) =
    role_allows(user, action)
    AND claims_revision_is_effective(user)
    AND ac_domain.site_id ∈ user.allowed_site_references
    AND observation.source_ac/domain is authorized
    AND relation_is_visible_at(observation.relation_revision)
```

AP 的可见 Site 不得默认取任意关联 AC 的 Site 并集：

```text
AP_SITE = any linked AC site   # 禁止默认采用
```

推荐默认规则：

1. AC/domain 先从现有 `device_inventory.id/site_id` 解析，不生成第二套 AC identity。
2. AP resource 可以跨 AC 复用，但每条 observation/binding 仍保留来源 AC 和来源 Site。
3. 用户只有 AC-A 所在 Site claim 时，只返回 AC-A 允许的 AP observation；不能因为同一 AP 也出现在 AC-B 而泄露 AC-B observation 或 group。
4. AP group 依附 AC/domain Site；跨 Site 聚合必须是显式授权的 union，并在响应中返回 scope summary/claims revision。
5. 无法判断来源、关联或 scope 时返回 `UNKNOWN`/`CONFLICT`/`403`，不静默扩权。
6. `SUPERADMIN` 的 global 只表示授权范围，不替代 AP identity、事实 freshness 或 topology evidence。

## 4. 需要冻结的业务决策

以下 7 项本轮保持 pending，必须在 R3-B0 之前批准：

1. `AcDomain` 的 key 和 `domain_kind` 如何从独立 AC、堆叠、集群、1+1 的实际证据确定。
2. serial/MAC 缺失、冲突、多接口和设备更换时的 AP identity 确认阈值。
3. 一个 AP 被多个 AC 观察时，用户可见的是授权 observation 子集还是经过规则的 AP 摘要；默认建议为“子集 + provenance”。
4. AP group 是否严格依附 AC/domain，是否允许跨 Site 聚合；默认建议禁止隐式聚合。
5. R/M、R/B、I 的 raw-to-normalized 映射、freshness deadline 和冲突处理人/规则。
6. 任务/报告的 Site scope snapshot 是否在任务创建时冻结，还是每次读取按当前 claims 重新裁决；默认建议任务创建时记录 snapshot，读取仍做当前权限复核。
7. 无线审计中是否允许引用 raw manifest/hash；默认允许引用，不写命令输出、凭据或敏感内容。

## 5. 必须的负向测试

- 单 Site 用户不能读另一 Site 的 AC、AP observation、AP group、task/run 或 audit。
- 同一 AP 跨 Site 关联时，不能通过 AP serial/MAC 查询绕过 AC Site claims。
- 过期/revoked/conflict claim 不能恢复历史授权。
- task/run 的 Site snapshot 与当前 claims 不一致时，响应必须可解释且不能越权。
- 无 AC source 或 relation conflict 的 AP 不能被全局搜索返回为已授权资产。

当前结论：`R3_SITE_AUTHORIZATION_READINESS=PARTIAL`；R2 claims 是可复用基座，但尚无无线 subject authorization contract。

