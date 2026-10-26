# P2B-3 Auth 与 Site Scope

## 现有 Auth 的事实

当前 Web Auth 只有 \`ADMIN\` 和 \`SUPERADMIN\`，以及 loopback local bearer token 映射的 \`SUPERADMIN\`。身份没有可靠的用户级 Site claims 或 Site membership 表，不能把任意用户默认伪造为 \`local\`、\`site-a\` 或全站用户。

## 当前可执行策略

- 匿名请求由现有全局 middleware 返回 401。
- \`SUPERADMIN\` 可读取 P2B3 全局候选数据，并可以显式使用 \`siteId\`/domain 等过滤器。
- \`ADMIN\` 不被伪造映射到某个 site；P2B3 返回 403 \`P2B_SITE_SCOPE_UNAVAILABLE\`，说明当前身份缺少可验证的 site scope。
- cross-site domain 只有 \`SUPERADMIN\` 在当前阶段可查看；domain scope 仍完整返回，不裁剪成会误导权限判断的部分视图。
- \`UNRESOLVED\`/\`CONFLICT\` domain 和 legacy pending address 可以被有权限的 \`SUPERADMIN\` 诊断读取，但不会获得 effective authorization 或 effective source。

这保持了 Phase 1 的既有设备读取 API 兼容，同时不把 Phase 1 的 ADMIN “可读设备库存”扩大解释为 P2B 网络规划数据的 site authorization。后续若要开放 ADMIN，必须先增加真实的用户 Site Scope 来源和 all-of 跨站判定。

## 防止泄漏

P2B3 的 ADMIN 拒绝在 repository 查询前完成；cross-site 403 不返回 domain count、device facts 或 source references。SUPERADMIN 的 \`siteId\` 过滤基于 \`network_domain_scopes\` 参数化 \`EXISTS\` 条件，不能用 site 名称或 device 的 legacy site 字段猜测 domain scope。
