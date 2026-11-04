# R3-B0 最终用户确认单

项目：`NetConsole-Next`  
任务：`NetConsole-Next-R3-B0-Contract-Amendment-And-Decision-Approval-Preparation`  
日期：2026-11-04  
当前状态：`APPROVED_DECISIONS=0`、`PENDING_DECISIONS=7`、`DESIGN_POLICY_STATUS=PROPOSED`

本单只请求用户确认业务规则。推荐项不是默认批准；用户回复前不进入 R3-B1 产品实现。

## 推荐摘要

| ID | 主题 | 推荐 | 关键约束 |
| --- | --- | --- | --- |
| R3_DECISION_01 | AC domain/topology | `OPTION_A` | 显式 AcDomain.id 引用已有 AC device IDs；topology 必须有成员/HA/IRF/cluster evidence |
| R3_DECISION_02 | AP serial/MAC identity | `OPTION_A` | serial + 完整 MAC exact evidence；缺失/冲突保留 candidate/conflict |
| R3_DECISION_03 | multi-AC AP visibility | `OPTION_A` | 只返回授权 observation 子集；摘要只基于可见事实 |
| R3_DECISION_04 | AP Group ownership/scope | `OPTION_A` | domain-shared evidence 才用 Domain key；否则 AC key；只读、不可写回 |
| R3_DECISION_05 | runtime state | `OPTION_A` | raw + normalized；失败不等 offline；stale/unknown/conflict 分离 |
| R3_DECISION_06 | Task/Report Site scope | `OPTION_A_REQUIRES_REVISION` | snapshot 是目标上限；执行/恢复/读取/下载/审计 intersect current claims |
| R3_DECISION_07 | audit provenance | `OPTION_A` | actor/action/subjects/revisions/manifest hash/reason；无 raw/credential/command text |

## 需要确认的执行含义

### AP Group

- Domain-owned 仅在明确 domain-shared evidence 存在时成立。
- 否则使用 AC-owned key；同名不合并，source key 缺失不生成稳定可合并 identity。
- Domain 成员变化不自动把 AC group 升级成 Domain group。

### Task/Report

- 创建时记录 snapshot、claims revision、authorized subjects、source AC/Site 和 actor。
- 有效执行目标为 `CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS`。
- 每个 connect、command/item、retry、resume、publish、read、download、audit 边界重查 claims。
- 撤销后不启动新目标、不自动补目标、不 retry。
- 不可安全中断的当前 bounded operation 只完成原子边界或安全中止，结果为 partial/`REVOKED_AFTER_START`。
- 新增 Site 权限不能扩大已有任务；完成后撤销也会阻止结果读取。

### R3-B1 authorization

- W01/W02/W03 从第一条 route 开始复用 P2B5。
- 服务端过滤 AC/AP observation，阻止 serial/MAC、AP ID、分页 total/count 和 generic Task/Report/Audit 旁路。
- API-W09 只复用内部 P2B5 predicate，不新增 claims 暴露接口。
- R3-B5 只负责跨 AC、partial visibility、复杂聚合和 no-leak 专项闭环。

## 可直接复制回复

```text
R3_DECISION_01=OPTION_A/B/C/DEFER
R3_DECISION_02=OPTION_A/B/C/DEFER
R3_DECISION_03=OPTION_A/B/C/DEFER
R3_DECISION_04=OPTION_A/B/C/DEFER
R3_DECISION_05=OPTION_A/B/C/DEFER
R3_DECISION_06=OPTION_A/B/C/DEFER
R3_DECISION_07=OPTION_A/B/C/DEFER
```

`DEFER` 表示不批准进入 R3-B1；它不是对推荐方案的默认接受。收到明确回复后，才更新 JSON/Markdown 的 `approvalStatus`、gate 和 R3-B1 准入状态。
