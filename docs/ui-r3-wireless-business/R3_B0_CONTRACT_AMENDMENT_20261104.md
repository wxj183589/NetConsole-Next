# R3-B0 Contract Amendment 2026-11-04

项目：`NetConsole-Next`  
任务：`NetConsole-Next-R3-B0-Contract-Amendment-And-Decision-Approval-Preparation`  
状态：`PROPOSED`，未冻结、未批准、未开始 R3-B1。

## 1. 修订目的

本轮只关闭三类设计歧义：

1. AP Group 的 Domain-owned 与 AC-owned 唯一身份边界。
2. Task/Report 在创建、执行中、完成后 Site claims 撤销的安全语义。
3. R3-B1 第一条无线资源 API 即具备服务端 P2B5 Site authorization 的准入门禁。

没有修改产品代码、数据库 schema、migration、无线 UI 或现有 R2 claims 实现。

## 2. 修订对照

| 范围 | 原规则 | 新规则 | 影响 API | 影响测试 | 影响批次 | 批准状态 |
| --- | --- | --- | --- | --- | --- | --- |
| AP Group identity | 原合同主要使用 `ac_domain_id + source_group_key` | 有明确 domain-shared evidence 才用 `DOMAIN|ac_domain_id|source_group_key`；具体 AC 或证据不足使用 `AC|source_ac_device_id|source_group_key`；缺 source key/ID 不合并 | API-W07/W08 | SC-28/29、SC-36..41 | R3-B4 | DECISION_04 pending |
| Task scope | 创建时 snapshot，读取时 current claims | snapshot 只限制最大目标；有效执行为 snapshot ∩ current claims；connect/item/retry/resume/publish/read/download/audit 都重查 | API-W10/W11；generic task/report/audit 不能旁路 | SC-31..34、SC-42..53 | R3-B1 prerequisite、R3-B6 full | DECISION_06 pending |
| Revocation in flight | 未明确不可中断操作 | 未开始目标不启动；可安全边界停止；不可安全中断的当前 bounded operation 只完成原子边界或安全中止，封存 partial，`REVOKED_AFTER_START`，不再后续操作 | API-W10/W11 | SC-45/46/50/52 | R3-B6 | DECISION_06 pending |
| First wireless authorization | 完整 Site authorization 放在 R3-B5 | R3-B1 W01/W02/W03 从第一条 route 复用 P2B5、过滤 observation、禁止 ID/serial/MAC bypass、无 count leak、fail closed | API-W01/W02/W03；API-W09 internal only | SC-24..27、SC-54..57 | R3-B1 | DECISION_03/06 pending |
| Site claims API | 拟议 `GET /api/system/wireless/site-claims` | 改为内部 `P2B5_SCOPE_PREDICATE`，不新增 route、不返回其他用户 claims | API-W09 | SC-57 | R3-B1/B5 | design only |

## 3. AP Group 正式合同

`display_name` 不是身份。服务端生成 opaque `ap_group_id`，由 scoped identity key 和 source revision 支撑：

```text
DOMAIN|ac_domain_id|source_group_key
AC|source_ac_device_id|source_group_key
```

Domain-owned 必须有厂商/domain-shared 或多成员一致的明确 evidence；同一 Domain ID、UI 层级或同名本身不足以升级为 Domain scope。不同 AC 同名组保持独立；同名不同 source key 不合并；source key 缺失则 candidate/unknown；Domain 成员变化不自动重写已有 AC groups。

## 4. Task/Report 撤销正式合同

创建时保存 actor、scope snapshot、claims revision、authorized subjects、source AC/Site IDs 和时间。snapshot 是目标上限，不是授权 token：

```text
EFFECTIVE_EXECUTION_TARGETS =
  CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS
```

每个受保护边界重新检查 claims。撤销后不启动新目标、不自动补目标、不 retry；当前无法安全中断的单个 bounded operation 仅允许完成原子边界或安全中止，结果标为 `REVOKED_AFTER_START`。完成后的结果也必须按当前 claims 重新授权；部分撤销只显示仍授权结果，不泄露 revoked Site 的记录、状态、数量、来源或 hash。

## 5. R3-B1 / R3-B5 分工

```text
R3-B1=基础无线资源授权，从第一条无线 route 开始
R3-B2=身份与关联授权扩展
R3-B3=状态聚合授权扩展
R3-B4=AP Group scope/membership authorization
R3-B5=跨 AC、partial visibility、复杂聚合、claims revision/no-leak 专项闭环
R3-B6=Task/Report 全面执行与读取授权
```

R3-B5 不再是首次添加无线 Site permission 的批次。

## 6. 不变项

- 7 项业务决策仍 `0 approved / 7 pending`。
- `POLICY_STATUS=PROPOSED`。
- `API_GAPS_BEFORE=10`、`API_GAPS_ACCOUNTED_FOR=10`、`API_GAPS_IMPLEMENTED_THIS_ROUND=0`、`API_GAPS_REMAINING=10`。
- API-W01..W08、W10..W12 仍设计未实现；API-W09 仅内部复用 P2B5，不是新 route。
- 真实设备、登录、网络扫描、真实任务、生产数据、schema/migration 均未触碰。
