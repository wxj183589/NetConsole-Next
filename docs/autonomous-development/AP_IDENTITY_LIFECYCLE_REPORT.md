# AP 身份与生命周期闭环报告

更新时间：2026-11-14（Asia/Shanghai）

## 结论

R3 已具备可复核的 AP 身份核验、生命周期状态和证据引用链路，但当前写入 authority 仍被明确限制在隔离临时数据根。不能将离线夹具和临时 JSON authority 宣称为生产生命周期数据库。

## 已验证边界

- 身份只由精确 Serial 与规范化 MAC 集合组成；不使用 AP 名称、模糊匹配、跨站点猜测或 `hzl10` 等站点硬编码。
- 当前 review 返回 identity revision、scope、状态、阻断原因、Observation/Raw 证据引用。
- 生命周期决策支持 `ACCEPT`、`REJECT`、`CONFLICT`、`SUPERSEDE`，要求 `SUPERADMIN`、当前 Site scope、乐观 revision 和属于当前 review 的证据引用。
- `ACCEPT` 会拒绝重复 Serial/MAC；`SUPERSEDE` 要求已接受的旧身份和当前 scope 可见的替代身份。
- 决策写入 bounded history，并写入 Catalog audit event；服务重启后可恢复当前状态与历史。
- 迟到的读取响应不会覆盖新的 AP 选择，页面只读展示当前真实 API 返回的数据。

## 当前实现状态

| 能力 | 状态 | 证据 |
| --- | --- | --- |
| 精确身份合同 | PASS | `WIRELESS_IDENTITY_CONTRACT_TESTS=3/3` |
| 投影身份冲突与跨 AC 观察 | PASS | `WIRELESS_PROJECTION_TESTS=PASS` |
| 生命周期决策、审计、重启恢复、过期 revision | PASS | `WIRELESS_IDENTITY_LIFECYCLE_TESTS=5/5 PASS` |
| 替换身份状态转换 | PASS | `WIRELESS_REPLACEMENT_TESTS=3/3 PASS` |
| 生产 schema-backed authority | NOT_VERIFIED | 当前 gate 只允许临时根，不允许生产数据根 |
| 生产 merge/split/replacement revoke | NOT_VERIFIED | 尚无生产写入授权和真实数据验收 |
| 跨进程并发写入 | NOT_VERIFIED | 当前 service 使用进程内锁，未宣称跨进程事务锁 |
| 真实设备身份验收 | NOT_VERIFIED | 本批未连接真实 AC/AP |

## 数据与安全边界

生命周期写入文件使用原子临时文件替换，并拒绝 `D:\NetConsoleData`、`D:\NetConsoleData-dev`、Reparse Point 和非临时根。当前实现只适合作为隔离候选 authority；在生产化前仍需定义正式 schema、迁移、并发、备份、回滚和 merge/split 审批门禁。
