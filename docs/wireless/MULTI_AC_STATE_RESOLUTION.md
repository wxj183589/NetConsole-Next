# 多 AC AP 状态解析

状态：设计契约，未接入生产采集。本文中的 `R/M`、`R/B`、`I` 仅作为当前研究假设的原始值示例；没有真实 H3C 命令输出、版本说明和时间语义证据前，不得把它们直接映射成生产状态。

## 1. 输出状态与原始状态分离

每个 AP 返回两组字段：

- `stateRaw`：AC 原样返回的状态，例如 `R/M`、`R/B`、`I`；永远保留；
- `state`：解析器在有明确 `stateSemanticsVersion`、新鲜度和身份匹配后给出的 `PRIMARY/BACKUP/UNKNOWN/STALE/CONFLICT`；
- `resolutionReason`：决定性证据、冲突或降级原因；
- `sourceAcs`：参与解析的 AC、观测时间、修订号和原始值。

当前未验证的语义版本固定标记为 `ASSUMPTION_PENDING_H3C_EVIDENCE`。生产开关默认不允许该版本把 `R/M` 提升为 `PRIMARY`；模拟测试可以显式打开“研究假设模式”验证算法的冲突和切换行为。

## 2. 解析输入

对每条观测先执行：

1. 校验 `site_id` 与 `ac_device_id` 绑定一致；不一致直接 `SITE_CONFLICT`；
2. 规范化序列号和主 MAC；非法或缺失身份只能进入未解析队列；
3. 按序列号优先、已信任主 MAC 次之做身份协调；两者指向不同身份时 `IDENTITY_CONFLICT`；
4. 检查 `observed_at` 与 `expires_at`；过期观测不参与当前候选，只留在历史；
5. 校验 `source_revision` 的单调性/幂等性，重复修订不重复计票；
6. 读取 AC 集群/主备关系，但不能把 AC 离线直接转换成 AP 离线。

## 3. 单个 FIT-AP 的候选选择

在同一 `fit_ap_id` 下，先按以下顺序筛选：

1. 同一 Site 且身份状态为有效；
2. 未过期且采集成功；
3. 状态语义版本受信任；
4. `observed_at` 在当前解析窗口内；
5. 以最新观测时间排序，时间相同按 `source_revision` 稳定排序。

如果没有满足条件的观测，输出 `STALE` 或 `UNKNOWN`，不能从旧值伪造当前在线状态。

## 4. 研究假设模式下的状态规则

以下表只在 `stateSemanticsVersion` 已被配置为受信任时生效；实际 H3C 证据可能需要替换映射。

| 原始状态 | 候选解释 | 约束 |
| --- | --- | --- |
| `R/M` | 当前主用候选 | 同时有两个新鲜 `R/M` 时冲突，不按采集顺序覆盖 |
| `R/B` | 备用候选 | 不能单独成为当前主用 |
| `I` | 未知/初始化候选 | 不覆盖另一 AC 的可信主用或备用观测 |
| 其他 | 未知 | 原样保留，解析结果为 `UNKNOWN` |

解析优先级：

1. 一个新鲜 `R/M` 且没有同窗口的第二个 `R/M` → `PRIMARY`；
2. 一个新鲜 `R/B` 且没有 `R/M` → `BACKUP`；
3. 一个 `R/M` 与一个 `R/B` → `PRIMARY` 取 `R/M`，`BACKUP` 保留为来源；
4. 两个或以上新鲜 `R/M` → `CONFLICT`，不自动猜主站；
5. 当前主 AC 离线但另一 AC 没有新的可信主用观测 → AP 仍显示最近解析状态并标记来源异常/不确定，不能直接变成 `OFFLINE`；
6. AP 身份冲突、Site 冲突或观测来源互相矛盾 → `CONFLICT` 优先级最高；
7. 过期观测全部过滤后 → `STALE`，同时显示最后可信观测时间。

## 5. 主备切换

当 AC-B 后续产生比 AC-A 更新的、同一 AP 身份的单一 `R/M` 观测，且 AC-A 的 `R/M` 已失效、降级或不在当前窗口，解析结果切换到 B。切换事件应包含：旧主候选、新主候选、各自观测时间、修订号、状态语义版本和解析器版本。若 A/B 的 `R/M` 时间重叠，不切换，进入 `CONFLICT`，等待下一次受控观测或人工处理。

## 6. AC 离线与 AP 状态

- AC 连接失败只记录在 AC 来源层；
- 只有 AP 自身有新鲜、可信的离线/失联观测，或所有来源观测均过期，才能投影 `STALE/UNKNOWN`；
- 不能因为一个 AC 不可达就把它最后一次看到的全部 AP 批量改成离线；
- 多 AC 场景必须展示“来源部分失败”，防止运维误把采集失败当成现场故障。

## 7. 冲突与隔离

以下情况统一进入冲突队列：

- 同一序列号对应不同主 MAC；
- 同一主 MAC 对应不同序列号；
- 同一身份在不同 Site 出现；
- 两个新鲜 AC 都报告主用；
- 同一 `source_revision` 的内容发生不可解释变化。

冲突期间列表保留原始观测，但禁止普通用户继续合并、覆盖凭据或修改替换链。处理动作需要 Site/身份管理权限、理由和审计。

## 8. 可重放伪代码

```text
resolve(observations, semanticsMode):
  valid = observations
    .where(site_is_consistent)
    .map(normalize_identity)
    .where(identity_is_resolved)
    .where(not_expired)

  if any_identity_conflict(valid): return CONFLICT
  if semanticsMode != TRUSTED_H3C_EVIDENCE and semantics_are_assumption(valid):
      return UNKNOWN_SEMANTICS with raw observations

  primary = valid.where(raw == R/M)
  backup = valid.where(raw == R/B)
  if primary.count > 1: return CONFLICT
  if primary.count == 1: return PRIMARY(primary[0], backup)
  if backup.count >= 1: return BACKUP(newest(backup))
  return UNKNOWN or STALE
```

实际实现不得只返回一个字符串；必须返回证据和原因，以便页面、审计和后续重放一致。
