# P2B-4 安全写入 API 设计

## 边界

P2B-4 是独立于 P2B-3 read API 的隔离写入能力。请求必须同时满足：`NETCONSOLE_NEXT_P2B_ENABLED=1`、`NETCONSOLE_NEXT_P2B_WRITE_ENABLED=1`、`NETCONSOLE_NEXT_P2B4_TEST_AUTH=1`、`NETCONSOLE_NEXT_P2B4_MIGRATION_AUTHORIZED=1`，数据库必须是显式 v14 isolated-test authority。默认关闭，v12 不自动迁移，生产和 `D:\NetConsoleData` / `D:\NetConsoleData-dev` 永远拒绝。

ADMIN 被拒绝，因为现有 Auth 没有可靠 site claims；只有 SUPERADMIN 在临时目录中的显式测试 authority 下可写。该权限不是生产授权模型。

## API

- `POST/PUT /api/system/devices/{id}/addresses[/addressId]`
- `POST /api/system/devices/{id}/network-memberships`
- `POST/PUT /api/system/vlan-plans[/id]`
- `POST /api/system/network-reviews`
- `POST /api/system/import-runs`，以及 `dry-run`、`confirm`、`cancel`、状态查询
- `GET /api/system/p2b4-operations/{id}`

所有写请求要求 bounded `idempotencyKey`。每个 operation 保留请求、状态、条目、revision、结果和 audit；重复相同请求返回原 operation，复用 key 但改变 payload 返回冲突。

## 不变量

- 不更新 `device_inventory.management_ip`、terminal profile 或 observed source fact。
- 地址冲突按 domain、family、规范化地址和半开 validity overlap 判断；prefix 不绕过冲突，同一设备的不同来源可以并存。
- VLAN 同 domain、同 VLAN、重叠 validity 的非 OBSERVED plan 冲突；相邻区间允许，不同 domain 允许，OBSERVED 与 planned 共存。
- review 只记录决定，不改写来源事实；revision/source identity 不匹配返回 `STALE_REVIEW`。
- direct write 和 import confirm 使用 SQLite Serializable transaction；失败时业务写入整体回滚。

## 恢复

服务启动会把 v14 `QUEUED/RUNNING` operation 标记为 `INTERRUPTED` 并写 audit。不会自动重放业务写入。
