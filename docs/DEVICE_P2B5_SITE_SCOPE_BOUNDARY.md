# P2B5 Site Scope / Admin Auth Boundary

日期：2026-10-27  
Branch：`codex-A/next-device-management-site-scope-20261027`  
基线：P2B5 commit `ed2a033`  
状态：Draft，安全边界设计；未开放 ADMIN 网络规划授权

## 当前事实

现有 Web Auth 只有 `ADMIN`、`SUPERADMIN` 和 loopback local bearer token（映射为 `SUPERADMIN`）。当前数据库和身份对象没有可信的用户级 Site membership/claims 来源。因此，P2B5 不把 ADMIN 的普通设备库存权限扩大解释为 Network Domain、地址或 VLAN 的 Site Scope。

## 强制边界

| 身份 | 网络规划读取 | 网络规划写入 | 依据 |
| --- | --- | --- | --- |
| 匿名 | 401 | 401 | 全局 Auth middleware |
| ADMIN，无可信 Site Claims | 403 `P2B_SITE_SCOPE_UNAVAILABLE` | 403/不可用 | 在 repository 查询前拒绝 |
| SUPERADMIN | 可读，可显式使用 site/domain 过滤 | 仅在 P2B4 隔离 v14 + gate 下可写 | 当前最高可信身份 |
| local bearer token | 按 SUPERADMIN 处理 | 仍受隔离 v14 + gate 限制 | 仅 loopback 本地入口 |

拒绝响应不返回 domain count、device facts、source reference 或跨站数据。不能通过 query/header 自报 site、不能用 `device_inventory.site_id` 猜 Network Domain scope，也不能以模糊名称匹配绕过 scope。

## 后续开放条件

只有在出现可验证的身份来源后，才允许设计 ADMIN scope：

1. 明确的用户主体与 Site membership/claim source；
2. Site/Station/Line/Section 的 all-of 判定和跨站冲突策略；
3. repository 查询前的授权裁决，拒绝时不执行数据查询；
4. 写入 API 复用同一 scope boundary，不能只在 UI 隐藏按钮；
5. 审计记录 actor、scope、decision、source 和 revision；
6. fixture 覆盖同站、跨站、缺失 claim、冲突 claim、过期 claim 和横向越权。

在这些条件满足前，UI 必须保留“网络规划只读能力不可用”或只读状态，不伪造 Site Claims、不显示写入按钮、不宣称 ADMIN 可用。

## 证据

现有 `tests/p2b3_read_api_e2e.py` 已覆盖 `SITE_SCOPE_AUTH=PASS`、`CROSS_SITE_DOMAIN_AUTH=PASS`，并确认 cross-site 403 不泄漏数据。P2B5 浏览器验收覆盖 Admin 无 scope 时的不可用页面和无“新增地址”按钮。该 Draft 只固化边界，不改变运行时授权。
