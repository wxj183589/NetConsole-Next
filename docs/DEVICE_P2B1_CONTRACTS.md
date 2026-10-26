# NetConsole Next P2B-1 契约冻结

状态：P2B-1 contract draft，待 reviewer 确认。本文冻结术语、身份、冲突、来源、错误和 feature gate 契约；不实现 Schema v13、不改变运行时 schema version、不新增业务 API 路由。

## 范围与要求

- P2B-1 只提供可供 P2B-2…P2B-5 复用的领域契约和纯规则。
- 设备身份继续使用现有 `device_inventory.id`；地址事实、地址 claim、VLAN 计划和来源事实不得另起一套设备主键。
- 所有规范化、冲突和有效期判断必须可重复、可测试，禁止模糊匹配和隐式覆盖。
- P2B-1 的 feature gate 默认为关闭，即使显式开启也不允许 schema mutation 或 API exposure。

## 冻结术语

| 术语 | 含义 |
| --- | --- |
| source fact | 某一来源、来源引用、revision 在某时刻提供的事实；同设备可保留多来源事实 |
| address claim | 设备对 `NetworkDomain + 地址族 + 规范化地址` 的占用主张；前缀是事实属性，不拆分 claim |
| Network Domain | 地址/VLAN 的业务作用域，可显式覆盖多个 site/station/line/section |
| validity | 半开区间 `[valid_from, valid_to)`；空起点/终点分别代表负/正无穷 |
| effective projection | 从来源事实投影出的当前业务视图；冲突不得自动选优先级 |
| manual review | 针对精确 source reference + revision 的人工决策，不接受模糊或过期选择 |

## 三项 P0 决策

| 决策 | 选择 | 拒绝的替代方案 |
| --- | --- | --- |
| `MULTI_SOURCE_ADDRESS_UNIQUENESS` | 同设备多来源事实共存；跨设备仅同显式 domain、同规范化地址、有效期重叠冲突 | 以来源优先级覆盖、以 site/IP 全局唯一、用前缀拆分占用；它们会丢失审计事实或误报跨域复用 |
| `LEGACY_IP_UNRESOLVED_DOMAIN` | legacy IP 无法推导 domain 时进入 pending/conflict，不自动猜测；重复 IP 必须人工解析 | 按 site 猜 domain、按设备名模糊匹配、直接写入默认 domain；均可能把错误归属固化进迁移 |
| `VLAN_VALIDITY_OVERLAP` | 同 domain + VLAN ID 的 active planned 区间重叠冲突；半开相邻不冲突；planned/observed 共存 | 全局 VLAN ID 唯一、闭区间、observed 覆盖 planned；无法表达跨域复用、时间边界或审计差异 |

## DB、API 与兼容影响

- DB：本提交不新增表、列、索引或 migration；v12 `device_inventory.management_ip` 仍是兼容读字段。
- API：本提交只定义 DTO、错误代码和 JSON 命名约定，不注册路由、不改变已有 API。
- 兼容：legacy record 保留 `device_inventory.id`、`management_ip`、`site_id`、`terminal_profile_id` 和确定性 source reference；未解析项不可冒充 resolved。
- 序列化：枚举使用稳定字符串；公开 DTO/错误字段使用 lower camel JSON 名称；冲突细节不回显设备 IP 等敏感事实。

## 测试要求与实现映射

- 地址、legacy、domain、VLAN、authority、error/gate 和 preflight 场景在 `P2B1ContractTests` 中以 synthetic fixture 驱动。
- 规则实现：`src/NetConsole.Next.Service/P2B1Contracts.cs`。
- 只读预检：`src/NetConsole.Next.Service/P2B1SchemaV13Preflight.cs`。
- 合成输入：`tests/NetConsole.Next.Service.Tests/fixtures/p2b1/p2b1_contract_fixture.json`。
- Reviewer 必须逐项确认三项 P0 决策、DTO 字段和迁移 blocker；在确认前状态保持 `REVIEW_PENDING`。

## Reviewer pending

待 reviewer 确认：三项 P0 决策、`NetworkDomainId` 为空时的 pending/conflict 边界、错误 DTO 的字段最小集，以及 v13 backfill 是否允许在下一阶段进入真实 migration 设计。
