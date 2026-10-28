# P2B-1 Schema v13 迁移前置只读预检

状态：synthetic preflight verified，真实生产数据未读取。本文不代表 Schema v13 已实现。

## 预检边界

`P2B1SchemaV13Preflight.Analyze(SqliteConnection)` 要求调用方传入已打开的只读连接，只执行 `SELECT`、`PRAGMA quick_check(1)`、`PRAGMA journal_mode` 和 `PRAGMA foreign_key_check`。它不创建表、不写入 row、不更新 schema_versions、不执行 migration。

## 输出字段

| 输出 | 语义 |
| --- | --- |
| `SCHEMA_V12_HEALTH` | v12 版本、quick_check 和 foreign key check 均通过时为 PASS，否则 BLOCKED |
| `LEGACY_DEVICE_COUNT` | `device_inventory` 总行数 |
| `LEGACY_ADDRESS_COUNT` | management_ip 非空行数 |
| `MISSING_SITE_REFERENCES` | inventory 左连接 sites 后找不到 site 的行数 |
| `DUPLICATE_ADDRESS_CLAIMS` | legacy 非空 management_ip 的重复地址组数量；v12 尚无 domain，不能直接判定合法跨域复用 |
| `UNRESOLVED_DOMAIN_COUNT` | 当前 v12 中缺少 Network Domain 的非空 legacy 地址数 |
| `EXPECTED_V13_BACKFILL_COUNT` | 预计需要进入 v13 pending/backfill staging 的非空 legacy 地址数 |
| `MIGRATION_BLOCKERS` | 健康、外键、缺 site、重复 claim review 等阻塞标识；unresolved domain 本身按选定 policy 进入 pending，不伪装成已解决 |

## 选择、替代与影响

- 选择：先只读审计 synthetic v12 fixture，再决定是否允许未来 migration design；不在预检阶段执行修复。
- 拒绝：预检自动补 site/domain、自动 deduplicate、直接跑 v13 migration；这些操作不可逆且会改变真实事实。
- DB：本提交不新增 `storage/schema_v13.sql`，不在 `Catalog.Initialize` 注册 v13，schema version 保持 12。
- API：预检报告只供测试/评审证据，不注册 production API route。
- 兼容：只读取既有 v12 `device_inventory.management_ip`，保留 legacy id/profile link 语义。
- 测试：隔离临时目录创建 v12 catalog，插入三个合成设备，使用 SQLite `Mode=ReadOnly` 读取并核对所有字段；确认不存在 version 13 或 v13 table。
- Reviewer pending：待确认重复地址组的统计口径和允许进入 v13 migration implementation 的 blocker 门槛。

## 当前合成结果

固定 fixture 预期：`SCHEMA_V12_HEALTH=PASS`、`LEGACY_DEVICE_COUNT=3`、`LEGACY_ADDRESS_COUNT=3`、`MISSING_SITE_REFERENCES=0`、`DUPLICATE_ADDRESS_CLAIMS=1`、`UNRESOLVED_DOMAIN_COUNT=3`、`EXPECTED_V13_BACKFILL_COUNT=3`；重复 legacy claim 进入 review blocker。以上仅来自隔离 synthetic v12 catalog，不是生产统计。

## Reviewer pending

未执行真实数据、未执行真实迁移、未修改 Dev Authority。Schema v13 仍为 `NOT IMPLEMENTED`，需要 reviewer 对三项 P0 决策和预检口径确认后再立项。
