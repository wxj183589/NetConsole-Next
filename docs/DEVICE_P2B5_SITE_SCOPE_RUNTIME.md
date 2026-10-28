# P2B5 Site Scope 运行时实现

日期：2026-10-27  
Agent：codex-A  
Branch：`codex-A/next-device-management-site-scope-runtime-20261027`  
状态：`IMPLEMENTED_AND_VERIFIED`

## 实现边界

- 复用现有 Web Auth 的稳定 `web_users.id` 作为 subject；不使用用户名、IP、设备名或请求参数推断 Site。
- `storage/schema_p2b5_site_scope.sql` 是辅助 schema，不被 `Catalog.Initialize` 自动注册，也不改变 Schema v13/v14 版本。
- 只有设置 `NETCONSOLE_NEXT_P2B5_SITE_SCOPE_MIGRATION_ALLOWED=1` 并通过临时路径/连接身份校验的隔离迁移 harness 才能创建 membership 表。
- `SUPERADMIN` 保留现有明确的全局范围；`ADMIN` 必须拥有当前有效的显式 Site membership。
- domain 的 Site scope 使用 all-of 判定：每个已解析 Site claim 都必须在当前用户的 active membership 内；未解析、冲突、过期、无 membership 均拒绝。
- P2B3 地址、membership、domain、VLAN 和 network-diff 查询在 SQL 层附加 scope predicate；拒绝发生在 repository 数据查询之前。
- P2B4 写入仍保持原有 `SUPERADMIN`、隔离 v14 authority、migration/test gate 和审计边界；本阶段没有把 ADMIN 写权限扩大。

## 验证

- Service Release build：PASS（仅保留既有 `SQLitePCLRaw.lib.e_sqlite3` NU1903 依赖告警）。
- Test Release build：PASS。
- `dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll`：PASS；包含 Site Scope auxiliary migration、稳定 subject、active/expired/conflict claim 和 SUPERADMIN global unit coverage。
- `python tests/p2b3_read_api_e2e.py`：PASS；既有无 Site authority 的 ADMIN denied boundary 与 SUPERADMIN read regression 保持通过。
- `python tests/p2b5_site_scope_e2e.py`：PASS；覆盖 missing authority、active ADMIN、cross-site reject、SQL filtering、expired/conflict reject 和 SUPERADMIN global HTTP flow。
- 未读取或写入 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未连接真实设备，未使用真实凭据，未执行生产迁移。
