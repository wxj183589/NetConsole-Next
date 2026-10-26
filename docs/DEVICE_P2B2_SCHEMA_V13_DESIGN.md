# P2B-2 Schema v13 candidate design

Schema v13 是增量存储候选，不改写 v1-v12 表，包含：

- `network_domains`：Network Domain、父域、VRF、来源、revision、生命周期和 scope resolution。
- `network_domain_scopes`：SITE/STATION/LINE/SECTION 的显式引用。迁移不伪造 Catalog 站点或线路实体。
- `device_domain_memberships`：设备与域的 PRIMARY/SECONDARY/SHARED 关系，带来源指纹和 revision。
- `device_addresses`：设备地址 source facts；保存 family、规范化地址、prefix、role、来源事实、域解析状态和冲突状态。
- `device_address_claims`：声明视图。没有全局 `UNIQUE(address)` 或 prefix-based unique；同地址是否冲突取决于域、设备、来源和有效期。
- `legacy_address_resolution`：旧 `device_inventory.management_ip` 的可追踪兼容记录，保存原值、terminal profile 链接、稳定 source reference 和 pending/conflict/resolved 状态。
- `vlan_plans` / `vlan_assignments`：计划 VLAN 与设备接口观测/分配事实；VLAN 1..4094、ACCESS/TRUNK/HYBRID/QINQ 结构和有效期由数据库约束保护。

所有候选记录均区分 `source_kind`、`source_reference`、`source_fingerprint`、`revision` 与 lifecycle。计划、观测和导入事实可以共存；有效投影和冲突决策仍由 P2B1 service rules 负责。本阶段不提供公开 API、写入 API 或 Vue 页面。

