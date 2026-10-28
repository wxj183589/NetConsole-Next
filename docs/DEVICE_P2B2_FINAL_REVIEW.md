# P2B-2 最终审查记录

## 审查范围

- PR25：Schema v13 candidate、v12→v13 隔离迁移、legacy `management_ip` backfill、事务恢复和重启兼容。
- PR25 的最终审查重点：迁移入口授权、数据库路径边界、连接路径一致性、Windows symlink/junction/reparse-point 防护，以及 v13 数据完整性。
- 本记录不是 GitHub formal approval。PR23、PR24、PR25 仍保持 Draft/Open，未合并。

## 结论

当前未发现未关闭的 P0/P1。此前仅使用 `StartsWith(tempRoot)` 的路径校验风险已在 PR25 追加提交中修复：迁移现在要求绝对路径、规范化后的临时目录边界、打开连接与迁移上下文同一路径，并逐级检查现有路径组件的 `ReparsePoint` 属性。

结论状态：`P2B2_SCHEMA_VERIFIED_APPROVAL_PENDING`

该状态只表示本地和 CI 证据已具备，不表示已获得正式审批，也不表示允许生产迁移。

## 数据与迁移边界

- 迁移只允许显式 `NETCONSOLE_NEXT_P2B_V13_MIGRATION_ALLOWED=1` 且 `IsolatedTest=true` 的上下文。
- 迁移目标必须在 `Path.GetTempPath()` 边界内；相似前缀、`..` 逃逸、其他盘符、UNC 路径和生产路径均拒绝。
- v12→v13 使用事务；schema、backfill 和 schema version 写入在同一事务中，fault/crash 注入后保持可重试。
- v13 backfill 只新增地址、claim、legacy resolution 和 schema 元数据，不覆盖原有 `device_inventory.management_ip`、设备信息或终端 profile。
- 正常启动不自动执行 v13 migration；v12、v13 版本差异仍由显式迁移流程处理。
- 本次验证没有读取或写入 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，没有连接真实设备或 VLAN。

## 证据

完整 .NET runner 已通过，包含 P2B-1 contract/preflight、v12→v13 backfill、关系/约束/完整性、幂等重跑、Catalog restart compatibility 和进程级 crash recovery。

新增路径安全断言全部通过：

- `MIGRATION_TEMP_PATH_ALLOWED`
- `MIGRATION_PATH_PREFIX_ESCAPE_REJECTED`
- `MIGRATION_DOT_DOT_ESCAPE_REJECTED`
- `MIGRATION_SYMLINK_ESCAPE_REJECTED`
- `MIGRATION_JUNCTION_ESCAPE_REJECTED`
- `MIGRATION_REPARSE_POINT_REJECTED`
- `MIGRATION_CONNECTION_PATH_MISMATCH_REJECTED`
- `MIGRATION_PRODUCTION_PATH_REJECTED`
- `MIGRATION_WITHOUT_EXPLICIT_FLAG_REJECTED`
- `MIGRATION_WITHOUT_ISOLATED_CONTEXT_REJECTED`

构建/测试保留仓库既有 `SQLitePCLRaw.lib.e_sqlite3` `NU1903` advisory；该 advisory 不改变本次迁移结论，但仍应由依赖治理单独处理。

## 审批与后续

PR23–PR25 没有 formal approval，不能将本记录升级为 approved。下一步是由独立审查者确认迁移安全边界和 v13 candidate 后，再决定是否进入 P2B3 read-only API；生产 v13 migration 仍必须另行授权和验证。
