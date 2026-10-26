# P2B-1 Network Domain 作用域契约

状态：contract draft，待 reviewer 确认。

## 要求

- `NetworkDomain` 是地址和 VLAN 的业务隔离域，不等同于 site、station、line、section，也不等同于 VLAN ID。
- 一个 domain 可以明确关联多个 site、station、line、section；跨 site/跨 station 是允许的业务形态。
- resolved scope 必须有显式 site references；unresolved/conflict scope 不得被当作可授权的 resolved scope。
- parent domain 引用必须无环且可解析；scope reference 不允许空值、控制字符或重复项。
- 严格可见性要求调用者拥有 domain 所有 site references；只允许部分 site 时拒绝跨站可见性。

## 选择、替代与影响

- 选择：domain 自带显式多层 scope，并以 site 集合执行权限边界。
- 拒绝：`一 domain=一 station` 会阻断共享服务域；`一 VLAN=一 domain` 会混淆传输标识与业务作用域；仅按用户当前 site 过滤会产生越权的部分视图。
- DB：未来 v13 需要可重建的 domain/scope 关系，但本阶段无 schema 文件、无迁移、无真实写入。
- API：domain DTO 保留 scope resolution、site/station/line/section references、parent reference 和 revision；跨 scope 错误统一使用 `NETWORK_SCOPE_CONFLICT`。
- 兼容：legacy IP 没有 domain 时保持 pending，不从 site 自动推断。
- 测试：序列化、跨 site visibility、跨 station membership、parent cycle、unresolved scope、重复/空 reference。
- Reviewer pending：待确认 parent domain 是否允许跨不同 `NetworkDomainKind`，以及 line/section references 的目录来源。

## scope 与授权

`P2BNetworkDomainScope.IsVisibleTo` 对 resolved domain 执行 all-of 检查：`SiteReferences.All(allowedSites.Contains)`。unresolved/conflict 默认不可见，只有显式的诊断模式才可允许查看 unresolved。`CanAssociateSite` 只接受 resolved 且 site 在显式集合中。

## P0 决策关联

本契约支撑 `MULTI_SOURCE_ADDRESS_UNIQUENESS`：地址 claim 的 domain 必须是显式、可审计的 scope；也支撑 `LEGACY_IP_UNRESOLVED_DOMAIN`：缺 scope 的 legacy 事实不能参与跨设备 claim 判定。替代方案及 DB/API/兼容/测试影响已在上述选择段冻结。

## Reviewer pending

跨站 domain 的最终授权策略尚未进入 API/UI；本阶段只提供纯规则和测试，不能据此宣称生产权限已接入。
