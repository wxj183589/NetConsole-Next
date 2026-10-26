# P2B-1 VLAN 有效期与冲突契约

状态：contract draft，待 reviewer 确认。本文是 `VLAN_VALIDITY_OVERLAP` 的权威说明。

## 要求

- VLAN ID 有效范围为 1..4094；0、4095 保留。
- 有效期统一为半开区间 `[valid_from, valid_to)`；空起点是负无穷，空终点是正无穷，非空必须 `from < to`。
- 同一 `NetworkDomainId + VLAN ID` 的 active planned claim 在有效期重叠时冲突；相邻区间不冲突。
- 不同 domain 可以复用同一 VLAN ID；planned 与 observed 同时存在时共存并进入 reconciliation，而非互相覆盖。
- archived claim 和不重叠历史 claim 不形成当前冲突。
- tagging、PVID、native VLAN、QinQ 字段必须按模式校验。

## P0：VLAN_VALIDITY_OVERLAP

- 选择：半开时间区间 + domain/VLAN 复合 key + source kind 区分 planned/observed。
- 拒绝：闭区间会让相邻配置产生假冲突；全局 VLAN 唯一会阻断跨域复用；observed 覆盖 planned 会破坏计划与现场差异审计。
- DB：未来 v13 需要存 interval、lifecycle、source revision 和 plan identity；本阶段不创建 `schema_v13.sql`。
- API：冲突返回 `VLAN_VALIDITY_OVERLAP`；跨域复用不报冲突；planned/observed 返回可比较的 coexistence 状态。
- 兼容：现有 v12 没有 VLAN 计划表，不执行 backfill。
- 测试：同域重叠、相邻、跨域、open start/end、invalid interval、planned/observed、PVID/tagging/QinQ。
- Reviewer pending：待确认 QinQ outer/inner VLAN 是否需要在下一阶段拆出独立 claim 维度。

## 模式校验

- ACCESS：PVID 必须等于 VLAN ID；native 可省略或等于 VLAN ID；不得有 QinQ 字段。
- TRUNK/HYBRID：native 与 PVID 同时出现时必须相等；不得有 QinQ 字段。
- QINQ：outer 必须等于 VLAN ID，inner 必须存在；不得有 native/PVID 字段。

## Reviewer pending

本阶段只冻结规则，不为设备端口生成实际配置，也不访问真实设备；执行、审批和回滚归入后续阶段。
