# P2B-2 crash recovery

测试 runner 提供独立子进程 `--p2b2-migrate`，可在以下 fault point 注入异常：

- before transaction
- after schema creation
- after first backfill
- after backfill before version registration
- after version registration before commit
- after commit（`Environment.FailFast`）

事务中断的前五种情形均在重开数据库后保持 v12，且不留下 v13 表或半条 backfill。提交后的进程崩溃保持完整 v13 事务；再次运行只返回 `AlreadyApplied`，不会重复地址、claim 或 version 行。这证明 crash boundary 是“Schema + backfill + version”原子提交，而不是启动时隐式恢复生产迁移。

