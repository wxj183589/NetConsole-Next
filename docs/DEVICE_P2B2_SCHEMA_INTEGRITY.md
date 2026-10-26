# P2B-2 Schema v13 integrity boundary

Schema v13 保持 SQLite `foreign_keys=ON` 和 WAL 运行方式。主要完整性保护包括：

- 所有设备、域、地址、claim、VLAN assignment 均通过显式外键关联；旧 Catalog 表保持原结构。
- 地址 family/prefix、VLAN 1..4094、tagging mode、时间区间和生命周期由 CHECK 约束保护。
- ACCESS/QINQ 的必要 PVID、outer/inner VLAN 关系使用 `IS NOT NULL` 显式判断，避免 SQLite CHECK 对 NULL 的三值逻辑漏洞。
- source fact 幂等键包含来源、reference、revision 和 fingerprint；地址 claim 不建立错误的全局地址唯一约束。
- v13 注册行只在 backfill 同一事务成功后写入。

验收执行 `PRAGMA integrity_check`、`PRAGMA foreign_key_check`、WAL 检查和边界约束插入失败测试。地址/VLAN 时间重叠是域内业务规则，由 P2B1 conflict rules 判定，不能用简单 UNIQUE 约束替代。

