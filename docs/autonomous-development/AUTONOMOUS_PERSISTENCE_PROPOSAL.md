# 持久化与任务边界独立方案（待评审）

日期：2026-11-05。本文是方案，不增加/执行 schema 或 migration。

现有 h3c_facts 只有 profile/kind/identity/source/captured 和 Current 标记，AP projection ID 来自当前 exact evidence。它无法可靠表达缺字段后的身份延续、replacement review、域成员生命周期、AP组稳定 source key、任务创建者/claims snapshot 或跨重启 Recent10。读取有限历史可以保留采集失败前的有效证据，本轮已实现；这不等于建立完整业务历史 Authority。

建议下一批独立评审如下增量模型，版本号须在最新主线核验后确定，不能在本轮自动应用真实数据：

| 对象 | 最小信息 | 不变量 |
| --- | --- | --- |
| Domain/member | 稳定domain_id、inventory device_id、成员/HA证据revision、有效期 | AC资产不复制；重叠或矛盾 fail closed |
| AP identity/evidence | 稳定AP ID、Serial、完整MAC集合及角色、candidate/conflict、replacement decision | 不以名称/IP/MAC后缀合并；缺证据不当矛盾 |
| Observation/relation | source AC/Site、captured/received、parser revision、原始状态、outcome、manifest ID/hash | 事实与推导分离；保留独立来源 |
| Effective state | AP+dimension、Current、PreviousEffective、Recent10 | 同一事务更新；只记录有效变化、最多10；失败不替代Current |
| AP group | source scope、稳定source key、显示名、shared evidence、membership revision | 未证实共享始终AC-owned；同名不是主键 |
| Task scope/items | stable actor user ID、创建时target/Site snapshot、claims revision、item outcome | EXECUTION_TARGETS=SNAPSHOT∩CURRENT；权限新增不扩张旧任务 |
| Audit | actor/action/subject/revision/reason、manifest ID/hash | 不存密码、token、命令原文或敏感输出 |

执行门禁：任务出队、connect前、command前、retry/resume前及结果/下载/报告读取均重新解析当前用户和claims。原子边界中撤销只能安全结束当前有界操作或中止，后续操作不得启动；记录REVOKED_AFTER_START，禁止自动重试扩大目标。

迁移计划：先最小fixture dry-run → exact identity collision/replacement审查 → backfill provenance证明 → crash/retry/idempotency/rollback测试 → 新旧读取兼容 → 独立用户数据应用授权。无确定来源时返回UNKNOWN/CONFLICT，不做猜测回填。不得复制整个Site/数据根，也不恢复Legacy HistoryStore。

freshness deadline 目前没有已验证设备配置值；下一批应定义显式来源/可配置策略与时钟异常规则，不能为本轮PASS随意假定时间阈值。空组发现、HA角色和共享组同样需要实际命令事实。本轮的STALE来自事实状态/离线来源/失败前旧证据，不声称已完成基于deadline的状态机。
