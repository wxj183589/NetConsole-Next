# P2B-4 network review 模型

review 是 append-only decision record，不是事实覆盖操作。记录 target type/id、source kind/reference、target revision、decision、reason、actor 和时间；允许 `ACCEPT_PLANNED`、`ACCEPT_OBSERVED`、`REJECT`、`DEFER`。

提交前读取目标当前 revision 和 source identity。任一不匹配返回 `STALE_REVIEW`，不插入 review，也不修改 address、membership、domain 或 VLAN。相同 actor、target、revision、decision、source reference 由唯一约束和 idempotency key 防止重复业务效果。
