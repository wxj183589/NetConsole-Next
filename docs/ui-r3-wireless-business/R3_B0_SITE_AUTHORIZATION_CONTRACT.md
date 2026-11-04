# R3-B0 无线 Site 授权合同

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

## 2. 四层无线 predicate

| 层 | predicate | 禁止事项 |
| --- | --- | --- |
| AC inventory | AC device/site 在当前 claims 内 | 用 AP name/serial 绕过 Site |
| AP observation | source AC/domain/site 与 revision 在 scope 内 | 返回未授权 observation 的 provenance |
| AP identity/state | 只从可见 observation 投影 | 用全局 identity 反推未授权来源 |
| group/task/audit | 继承 source relation 与当前 claims | 用 generic task/audit list 绕过 Site |

结果可以是 `FULL`、`PARTIAL` 或 `NONE`。`PARTIAL` 只能说明“当前响应为授权子集”，不能暴露被过滤对象的名称、数量、状态、时间或 hash。

## 3. Task/report scope

推荐 `R3_DECISION_06=OPTION_A`：

- 创建时记录 `scope_snapshot`、`claims_revision`、source AC/domain IDs 和 actor。
- 执行按 snapshot 记录目标，以保证可重现；不允许把运行中权限变化静默变成新目标。
- 读取、下载、审计查询重新解析当前 claims；已撤销 Site 的结果不可读取。
- response 同时返回 `created_scope_status` 和 `current_scope_status`；若两者不同，标记 `REVOKED`/`PARTIAL`。
- generic `/api/system/tasks` 不能成为 wireless task result 的旁路；无线 task API 必须重新做 subject predicate。

## 4. 错误和审计

推荐错误：

```text
401 AUTH_REQUIRED
403 P2B_SITE_SCOPE_DENIED / WIRELESS_SCOPE_DENIED
404 RESOURCE_NOT_VISIBLE
409 RELATION_REVISION_CONFLICT
422 IDENTITY_OR_STATE_CONFLICT
503 WIRELESS_AUTHORITY_UNAVAILABLE
```

权限拒绝可按现有审计边界记录 actor、action、bounded subject、scope revision 和 reason code；不得记录凭据、命令输出或未授权资源细节。依据：`docs/DEVICE_AUDIT_MODEL.md:1-32`。

## 5. SUPERADMIN 规则

SUPERADMIN 的 global scope 只代表当前 authority 的全局授权，不改变 source provenance，不允许绕过 identity conflict、relation revision 或敏感数据排除规则。

## 6. B0 边界

B0 只定义 predicate、snapshot、错误、测试和 API 合同；不修改 claims schema、`P2B5SiteScope`、数据库或现有 R2 device management。
