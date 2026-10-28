# P2B-2 legacy management_ip backfill

每个非空 `device_inventory.management_ip` 都保留原始文本并生成一个稳定的 `legacy-management-ip:<deviceId>` source reference。IPv4 使用 `/32`，IPv6 使用 `/128`，规范化地址由 P2B1 address normalization 生成；不把旧地址解释成 Network Domain。

- 规范化地址只被一个设备声明：`LEGACY_ADDRESS_PENDING_DOMAIN`，等待显式域绑定。
- 多设备声明同一规范化地址：全部进入 `LEGACY_ADDRESS_CONFLICT`，并使用 `ADDRESS_ALREADY_CLAIMED`，不自动按站点拆分。
- terminal profile ID、原 management IP、设备 ID 和更新时间保留在 resolution 记录中。
- pending/conflict 记录没有伪造 `network_domain_id`，因此不能被误读为已解析的有效地址。

本 candidate 不做真实数据目录回填，也不触碰 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。

