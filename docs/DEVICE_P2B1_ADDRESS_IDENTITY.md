# P2B-1 地址身份与 legacy IP 兼容契约

状态：contract draft，待 reviewer 确认。本文是 `MULTI_SOURCE_ADDRESS_UNIQUENESS` 与 `LEGACY_IP_UNRESOLVED_DOMAIN` 的权威说明。

## 要求

1. 地址规范化后才参与比较；IPv4 与 IPv6 是不同地址族，IPv4-mapped IPv6 归一到 IPv4，不能绕过冲突。
2. source fact identity 使用 `device_id + NetworkDomainId（legacy pending 可为空）+ address family + normalized address + prefix + role + source kind/reference/revision`。
3. 同一来源的同一 revision/fingerprint 重复导入是幂等 duplicate；同一 source reference 的不同 revision 必须保留历史。
4. address claim 使用 `NetworkDomainId + address family + normalized address`；prefix 不拆分 claim。active validity 重叠才形成同时占用。
5. 同设备多来源共存；不同设备同显式 domain 重叠冲突；不同显式 domain 可复用；缺 domain 不得自动形成 claim。
6. 禁止按设备名、site 名、IP 前缀或相似文本做 fuzzy identity。

## P0：MULTI_SOURCE_ADDRESS_UNIQUENESS

- 选择：source facts 与 address claims 分离。事实可以多源并存，claim 在明确 scope 内判重。
- 拒绝：全局 `IP` 唯一会拒绝合法跨 domain 复用；按 prefix 唯一会允许同 IP 逃避冲突；来源优先级覆盖会损失审计历史。
- DB 影响：v13 未来需要保留 source/revision/validity 与 claim scope；本阶段不落表、不迁移。
- API 影响：DTO 暴露 `addressFamily`、`normalizedAddress`、`prefixLength`、`sourceReference`、`revision`、`conflictState`；不把 prefix 作为 claim identity。
- 兼容影响：现有 `management_ip` 继续作为 legacy read；旧记录需先进入 pending/conflict 再解析 domain。
- 测试：同设备多来源、重复导入、历史 revision、同域/跨域/非重叠、不同 prefix、IPv4/IPv6、source coexistence 和 claim conflict。
- Reviewer pending：等待确认 claim key 是否保持为三元组，以及 role 是否允许未来扩展为同地址多角色。

## P0：LEGACY_IP_UNRESOLVED_DOMAIN

- 选择：legacy 单 IP 可规范化但 domain 为空时标记 `LEGACY_ADDRESS_PENDING_DOMAIN`；同 IP 跨设备冲突时标记 `LEGACY_ADDRESS_CONFLICT`，不自动猜测。
- 拒绝：默认 `local`、按 site 自动映射、按 terminal profile 或名称模糊关联；这些策略不能证明业务域归属。
- DB/API/兼容：保留 device id、management_ip、site、terminal profile link、updated time 和 `legacy-management-ip:<deviceId>` source reference；resolve 只能显式写入 Network Domain。
- 测试：单 IP pending、跨 site 重复 IP conflict、重复 Plan 幂等、profile/id 保留和 compatibility read。
- Reviewer pending：重复 IP 的人工处理界面属于 P2B-2 以后，本阶段只冻结状态与错误语义。

## 规范化与比较

`P2BAddressNormalization` 校验地址族和 prefix 边界（IPv4 0..32、IPv6 0..128），用 `IPAddress` 产生稳定文本。有效期使用 `[from,to)`；空起点/终点为无穷。active claim 的重叠是冲突，archived 或不重叠是历史复用。

## Reviewer pending

迁移执行、domain 解析 UI、真实 legacy 数据统计均未在本阶段发生；需 reviewer 先确认上述两个 P0 决策后，才允许进入 v13 migration implementation 评审。
