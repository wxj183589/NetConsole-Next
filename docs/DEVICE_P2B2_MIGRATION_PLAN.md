# P2B-2 v12 → v13 migration plan

## 授权边界

迁移入口只接受显式 `P2B2MigrationContext.ForIsolatedTest`，并要求环境变量 `NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED=1`。数据库必须位于临时目录且必须与打开连接的绝对路径一致。正常 `Catalog.Initialize`、P2B feature gate 和生产启动不会调用该入口。

## 事务顺序

1. 读取并确认当前最高 schema version 为 12。
2. 在 `Serializable` 事务中执行嵌入的 additive `schema_v13.sql`。
3. 从 `device_inventory.management_ip` 生成稳定 legacy source reference、地址事实、claim 和 resolution 记录。
4. 所有 backfill 成功后才插入 `schema_versions(13)`。
5. 提交事务；提交后不得再自动修改生产数据。

重复运行在版本 13 时只做结构校验并返回已应用结果；backfill 使用稳定主键和 `INSERT OR IGNORE`，不生成重复事实。v12 schema、旧管理 IP 和 terminal profile 表不被重建或删除。

