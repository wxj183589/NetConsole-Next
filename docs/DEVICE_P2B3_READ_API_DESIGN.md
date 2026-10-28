# P2B-3 网络规划只读 API 设计

## 范围

本分支只实现服务端只读 API，不实现 P2B4 写入/import/review，也不实现 P2B5 Vue 页面。API 只读取已经存在且通过完整表校验的 Schema v13；不会把 v12 数据自动迁移成 v13。

| API | 返回内容 |
| --- | --- |
| \`GET /api/system/devices/{id}/addresses\` | 地址 source facts、validity、revision、冲突状态和安全的 effective projection |
| \`GET /api/system/devices/{id}/network-memberships\` | 设备与 Network Domain membership、domain scope 和解析状态 |
| \`GET /api/system/network-domains\` | 有界 Network Domain inventory、scope references、来源和生命周期 |
| \`GET /api/system/vlan-plans\` | VLAN plan、有效期、source provenance、scope 和 planned/observed conflict state |
| \`GET /api/system/devices/{id}/network-diff\` | planned/imported/manual 与 observed 的设备级比较和安全 diff state |

所有查询均使用独立的 SQLite \`ReadOnly\` connection 和 \`PRAGMA query_only=ON\`。API 层不调用 Catalog 写入方法，不修改 \`device_inventory.management_ip\`、v13 facts、schema version、VLAN 或 membership。

## Feature gate 与 schema 边界

\`NETCONSOLE_NEXT_P2B_ENABLED\` 不为 \`1\` 时，API 返回 503 \`P2B_SCHEMA_UNAVAILABLE\`，reason 为 \`FEATURE_DISABLED\`。打开 gate 但数据库仍是 v12 时，API 仍返回同一 controlled 503，且不会调用迁移。

只有 gate 打开、schema version 为 13 且所有 P2B2 v13 表存在时，API 才可用。P2B3 不复用 P2B2 migration entry point，也不在正常启动路径中注册 migration。

## 读取语义

- source facts 保留 \`sourceKind\`、\`sourceReference\`、\`revision\`，不合并覆盖 planned/observed。
- effective projection 只有在事实值没有冲突且 scope 已解析时才提供；多源相同值不擅自选择 source，冲突或 unresolved 时 effective source 为 null。
- legacy backfill 产生的 \`PENDING_DOMAIN\`/\`LEGACY_ADDRESS_PENDING_DOMAIN\` 行仍保留并可由有权限的诊断读取；它们不会被推断成已授权 domain。
- network diff 将“planned only”与“observed only”分开；缺少 observed 不等于当前设备不合规。
