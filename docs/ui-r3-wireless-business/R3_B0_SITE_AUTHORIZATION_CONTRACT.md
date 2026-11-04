# R3-B0 无线 Site 授权合同（修订版）

修订日期：2026-11-04
状态：`PROPOSED_PENDING_APPROVAL`。R2 `P2B5SiteScopeStore` / claims 是唯一 Site authority；本文件不创建第二套无线权限表。

## 1. Authority 与 predicate

无线对象的可见 Site 通过 source AC/device/profile 的既有 Site claim 解析：

```text
user identity + current claims revision
  -> authorized site references
  -> AC device/profile in scope
  -> AC domain members in scope
  -> AP observation source AC in scope
  -> AP identity/state/group/task/audit projection
```

必须复用 `P2B5SiteScopeStore.Resolve(CurrentIdentity(context))`、`DeviceSiteScopeGuard` 和现有 fail-closed behavior。证据：`src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`、`src/NetConsole.Next.Service/Program.cs:1068-1124`。

## 2. R3-B1 从第一条无线 API 开始的最低授权

R3-B1 的 AC domain、AP resource、AP observation list/detail 都属于受保护业务资源，不能等到 R3-B5 才首次加权限。每条 API 必须在服务端完成以下顺序：

1. 解析当前身份和 P2B5 claims；claims 缺失、冲突、过期或 authority 不可读时 fail closed。
2. 验证请求涉及的 AC device/domain 与当前 Site scope 的关系；客户端不得传入或覆盖 claims。
3. 过滤 AP observation，再生成 AP identity、state、group 或 count 摘要；未授权 observation 不参与任何聚合。
4. 对 list 只返回授权后的 items 和 cursor；不得返回全局 total、隐藏对象 count、未授权 Site 名称或时间范围。
5. 对 detail 使用 opaque ID + source predicate；猜测全局 AP ID、serial 或 MAC 只能得到不可区分的 `404 RESOURCE_NOT_VISIBLE`，不能泄露对象存在性。
6. serial/MAC search 只能在已授权 observation 集合内执行；不能把 identity lookup 当成 Site claims 旁路。
7. 每个响应绑定 `claimsRevision`、`scopeStatus`、`asOf` 和 source/relation revision。

R3-B1 最低门禁：

```text
P2B5_REUSE_FROM_FIRST_WIRELESS_ROUTE=REQUIRED
AC_SCOPE_SERVER_SIDE=REQUIRED
AP_OBSERVATION_FILTER_SERVER_SIDE=REQUIRED
UNAUTHORIZED_COUNT_LEAK=FORBIDDEN
SERIAL_MAC_AUTH_BYPASS=FORBIDDEN
GLOBAL_AP_ID_DETAIL_BYPASS=FORBIDDEN
FAIL_CLOSED_ON_SCOPE_FAILURE=REQUIRED
```

## 3. 四层 wireless predicate

| 层 | predicate | 禁止事项 |
| --- | --- | --- |
| AC inventory | AC device/site 在当前 claims 内 | 用 AP name/serial 绕过 Site |
| AP observation | source AC/domain/site 与 revision 在 scope 内 | 返回未授权 observation provenance |
| AP identity/state | 只从可见 observation 投影 | 用全局 identity 反推未授权来源 |
| group/task/audit | 继承 source relation 与当前 claims | 用 generic task/audit list 绕过 Site |

结果可以是 `FULL`、`PARTIAL` 或 `NONE`。`PARTIAL` 只能说明“当前响应为授权子集”，不能暴露被过滤对象的名称、数量、状态、时间或 hash。

## 4. Task/report scope 与权限撤销

推荐 `R3_DECISION_06=OPTION_A_REQUIRES_REVISION`：

创建成功时记录：

```text
task_id
actor_id
scope_snapshot
claims_revision_at_creation
authorized_subject_ids
source_ac_device_ids
source_site_ids
created_at
```

`scope_snapshot` 是任务允许涉及的最大目标集合，不是永久授权凭证。新增 Site claims 不能自动扩大已有任务目标。

实际执行目标为：

```text
EFFECTIVE_EXECUTION_TARGETS =
  CREATION_SCOPE_SNAPSHOT INTERSECT CURRENT_AUTHORIZED_TARGETS
```

在每个需要受保护权限的边界重新解析当前 claims：连接前、每个 command/item 前、retry 前、resume 前、report publish 前。权限撤销后：

- 尚未开始的目标不再启动，item 标记 `REVOKED`，记录安全审计 reason code。
- 已在可安全取消边界内的目标停止并标记 `REVOKED`/`CANCELLED_BY_SCOPE`，不自动 retry。
- 已经进入不可安全中断的单个有界操作时，只允许完成该已接受的原子边界或按 transport 安全中止；必须封存 partial Raw/result，标记 `REVOKED_AFTER_START`，不再启动后续 command、retry、report publish 或新目标。
- 任何上述结果都不是设备故障，不得伪装为 offline、success 或完整 task。
- 任务恢复必须重新检查当前 claims；旧 revision 不能单独授权 resume。

读取、下载、审计和报告查询再次验证当前 claims。已撤销 Site 的结果不得通过 detail、list、result、download、generic task 或 audit 入口继续读取。部分撤销时只返回仍有权限的 Site 结果，并标记 `CURRENT_SCOPE_STATUS=PARTIAL`；被撤销 Site 的记录、状态、数量、来源和 hash 不进入响应或聚合。

## 5. Generic API 旁路规则

现有 `GET /api/system/tasks`（`Program.cs:659`）是 generic task history，当前没有 wireless subject predicate；因此不能成为 R3 wireless result 的授权入口。未来实现必须：

- 对 wireless subject 重新应用当前 Site predicate，或将 wireless task 从 generic list 中排除并要求 API-W10；
- 对 Task Detail、Report、Download、Result、Audit 逐一执行相同的 subject authorization；
- 不因 actor 是创建者、旧 claims revision 或拥有 profile role 而跳过当前 claims。

## 6. API-W09 结论：内部复用，不新增 claims 暴露接口

现有 P2B5 Site scope 已能提供服务端授权判断，本轮不新增 `GET /api/system/wireless/site-claims`。API-W09 保留为内部合同标识：

```text
API-W09=INTERNAL_P2B5_SCOPE_PREDICATE
NEW_ROUTE=NO
CLIENT_SUPPLIED_CLAIMS=FORBIDDEN
OTHER_USER_CLAIMS=NEVER_RETURNED
UNAUTHORIZED_SITE_NAMES=NEVER_RETURNED
```

如果未来确实需要 UI 摘要，也只能返回当前身份已授权的 bounded status，不能成为权限来源或暴露内部 claims 细节；该接口需另行授权，不属于 B0/B1 实现。

## 7. 错误和审计

```text
401 AUTH_REQUIRED
403 P2B_SITE_SCOPE_DENIED / WIRELESS_SCOPE_DENIED
404 RESOURCE_NOT_VISIBLE
409 RELATION_REVISION_CONFLICT
422 IDENTITY_OR_STATE_CONFLICT
503 WIRELESS_AUTHORITY_UNAVAILABLE
```

权限拒绝可按现有审计边界记录 actor、action、bounded subject、scope revision 和 reason code；不得记录凭据、命令输出或未授权资源细节。依据：`docs/DEVICE_AUDIT_MODEL.md:1-32`。

## 8. R3-B5 的新定位

R3-B5 不再是“首次实现无线权限”，而是：

```text
R3-B5=跨 AC、部分可见性、复杂聚合、claims revision 与 no-leak 专项闭环
```

R3-B1 负责基础 wireless resource authorization；R3-B2/R3-B3/R3-B4 分别扩展 identity/association、state、group authorization；R3-B6 负责 task/report 全面 scope enforcement。

## 9. B0 边界与依据

B0 只定义 predicate、snapshot、execution boundary、撤销、错误、测试和 API 合同；不修改 claims schema、`P2B5SiteScope`、数据库或现有 R2 device management。

- `src/NetConsole.Next.Service/P2B5SiteScope.cs:20-144`
- `src/NetConsole.Next.Service/Program.cs:659,918-928,1068-1124`
- `src/NetConsole.Next.Service/Catalog.cs:1418-1468,1649-1685`
- `docs/ui-r3-wireless-business/R3_B0_BUSINESS_DECISION_PACKAGE.md`：`R3_DECISION_03`、`R3_DECISION_06`。
