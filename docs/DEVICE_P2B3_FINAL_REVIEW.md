# P2B-3 最终代码审查

## 结论

本次审查确认两个 P1 并完成修复：

1. Diff 集合使用 SequenceEqual，来源事实顺序变化会把相同集合误判为 MISMATCH。
2. Diff 查询按全局 101 条截断；当同一 Domain/Interface 的事实跨越截断边界时，可能错误返回 PLANNED_ONLY、MATCH 或 MISMATCH。

同时修复了分页 Cursor 未绑定过滤器的问题。Cursor 现在携带过滤指纹，改变过滤条件复用旧 Cursor 会返回 INVALID_QUERY。

## 审查结果

- P0：0
- P1：2 found / fixed
- P2：保留 NU1903 advisory、Vite large chunk warning；不扩大本次范围
- Site Scope：当前 Auth 只有 ADMIN/SUPERADMIN，没有可靠 per-user site claims；ADMIN 保持拒绝，SUPERADMIN 才能使用 P2B-3 scoped reads
- Feature Gate：disabled 返回 503；v12 返回 503 且不迁移；有效 v13 才开放读取
- GET：使用 read-only SQLite connection 与 PRAGMA query_only=ON，不执行 schema/catalog/device/management_ip/VLAN 写入

## 状态

PR #26 仍为 Draft/Open/Unmerged，正式 reviewer approval 仍为 PENDING。本轮只向 PR #26 开发分支追加修复，不改变 PR 状态，不合并 main。

P2B3_REVIEW_STATUS=P2B3_READ_API_REVIEWED_APPROVAL_PENDING
