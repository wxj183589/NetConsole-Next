# P2B-4 Schema v14 迁移

v14 是 additive isolated migration，新增 `p2b4_authority`、`p2b4_operations`、`p2b4_operation_items`、address/VLAN history、`network_reviews` 和 `network_import_runs`。没有修改 v13 migration，也没有在 `Catalog.Initialize()` 注册 v14。

迁移入口是测试 runner 的 `--p2b4-migrate <database> <data-root>`，要求数据库当前为 v13、`IsolatedTest=true`、数据库和 data root 位于临时目录、路径逐级没有 reparse point，并且显式设置：

- `NETCONSOLE_NEXT_P2B4_MIGRATION_ALLOWED=1`
- `NETCONSOLE_NEXT_P2B4_TEST_AUTH=1`

schema、authority row 和 version 14 在一个事务中写入，可幂等校验。普通启动遇到 v12/v13 不会自动升级；P2B-4 write repository 只接受 authority root 匹配的 v14 临时数据库。
