# 设备 CSV 并发与资源边界

## 资源上限

| 项目 | Phase 1 限制 | 超限行为 |
|---|---:|---|
| 原始 UTF-8 输入 | 2 MiB | 受控 `429` |
| 数据行数 | 10,000 | 受控 `429` |
| 单字段字符数 | 4,096 | 受控 `429` |
| 进程内 active preview | 32 | 受控 `429` |
| 单管理员 active preview | 8 | 受控 `429` |
| commit receipt | 64 条、有界、10 分钟 | 最老记录淘汰 |
| preview lifetime | 10 分钟 | token 失效 |

preview 只保存结构化候选，不保存明文凭据；服务重启会丢弃进程内 preview，commit 返回不存在/过期，不恢复不确定状态。

## Preview 状态机

```text
CREATED -> COMMITTING -> COMMITTED(receipt)
                    \-> AVAILABLE(rollback, safe retry)
EXPIRED -> discarded
```

- `CREATED` 由当前 SUPERADMIN 创建，并绑定 actor。
- 只有一个请求可以在 gate 内把状态转成 `COMMITTING`。
- 事务成功后移除 preview，写入短期 receipt；相同 actor 重试返回原结果，不重复写库。
- 事务失败先 rollback，再恢复 `AVAILABLE`；其它请求不会在事务中途消费 token。
- 其它管理员不能使用该 token。

## 解析规则

- 支持 UTF-8 BOM、CRLF、LF、quoted field、escaped quote 和 quoted record。
- Header 不得为空、重复或未知；必须包含 `name`、`managementIp`。
- 每行字段数必须与 Header 完全一致；不静默截断或补齐。
- 拒绝非法闭引号、未闭合引号、超长字段、重复 device ID、重复 Profile、ID/IP 错配和公式样式值。
- `site_id + management_ip` 是匹配和唯一性作用域；设备 ID 始终优先保持稳定。

## Preview → Commit 一致性

更新候选保存 preview 时的 `device_inventory.updated_ms`。commit 使用 Serializable transaction 和 `WHERE id=$id AND updated_ms=$expectedUpdated`；目标在 preview 后被编辑、停用、换组、换 Profile 或 IP 时，commit 返回冲突并整体回滚。

创建候选在 commit 时重新确认 Site、Group、Profile、device ID 和 IP；SQLite constraint 或应用冲突均不得产生部分提交。
