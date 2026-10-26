# P2B-2 compatibility

兼容性策略是 additive candidate：

- v1-v12 表和 `Catalog.Initialize` 仍按原路径工作；v13 不加入正常启动 migration chain。
- `device_inventory.management_ip`、旧设备查询、设备详情、h3c profile host 和 terminal history 保持原值与原 ID。
- v12 `device_operations`、audit、collection source、terminal session 表不重建；现有 repository/read model 不需要知道 v13 即可启动。
- `NETCONSOLE_NEXT_P2B_ENABLED` 只控制 P2B feature exposure，不等于 Schema migration authorization。
- v13 的内部 repository 只提供 candidate 读边界；P2B-3 public read API、P2B-4 write/import API 和 P2B-5 Vue 均未实现。

因此该分支可以用于隔离 schema review，但不能被当作已授权生产升级包。

