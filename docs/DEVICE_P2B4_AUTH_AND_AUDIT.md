# P2B-4 认证、隔离与审计

P2B-4 的 write gate 与现有 read gate 独立：read gate 开启不会开启写入。除 read gate 外还必须显式开启 write/test-auth/migration-authorized 环境变量；缺任一项均不可写。

当前 Auth 没有可靠 site claims，因此 ADMIN 明确返回 `P2B4_ADMIN_WRITE_DENIED`。SUPERADMIN 也只有在 v14 `p2b4_authority.mode=ISOLATED_TEST`、root 位于临时目录、authority root 与运行 data root 一致时才可写。v14 migration 本身要求 isolated context、临时数据库边界、无 reparse path，以及两个显式 migration/test-auth 环境变量。

每次 preview、confirm、cancel、direct write、冲突、拒绝和 restart recovery 都写 `audit_events`。audit 只保存 actor、operation/import id、entity、结果和错误码，不保存 CSV、密码、token 或完整请求 payload。v12/v13 运行时不自动执行 v14 migration。
