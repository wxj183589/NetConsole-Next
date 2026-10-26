# NetConsole Next 设备管理 Phase 1 独立审查

日期：2026-10-26
目标 PR：`#21` 设备管理 Phase 1  目前参考 HEAD：`4eb01463ebc5fba7277fe4b5912942ea649eda17`
审查 worktree：`codex-A/pr21-device-safety-review-20261026`
基线：PR #20 `8e7aff41b2473a35bb767478c625b66a5ea975ba`

## 结论

本轮独立审查发现 4 项属于 Phase 1 直接范围的 P1 数据一致性/并发问题，均已在审查 worktree 修复并加入回归测试：

1. 设备资料 `enabled` 原先会联动写入 Terminal Profile 和 Collection Source。
2. 同一个 CSV preview token 没有原子领取状态，重复提交可能重复更新或产生竞态。
3. CSV 解析会静默忽略多余字段、覆盖重复表头，且 preview 后更新没有版本校验。
4. v11/legacy Profile bridge 会在同一局点/IP 下生成重复库存，GET 查询还会隐式写库。

修复后的本地 Service build、C# harness 和隔离数据回归通过。GitHub 正式 Review 仍需 reviewer 在最新 HEAD 上确认；本文件不把 CI PASS 或零线程误报为 GitHub APPROVED。

## 发现与修复

### P1：设备资料状态错误联动采集源

触发条件：为库存设备创建、编辑、停用或重新启用时带有 `terminalProfileId`。

原行为：`SyncTerminalProfileEnabled` 同时修改：

```text
device_inventory.enabled
h3c_device_profiles.enabled
collection_sources.enabled
```

影响：资料层停用会改变终端连接许可和采集源调度许可；重新启用还可能恢复管理员此前手工停用的 Profile/Source。Profile 重新关联时旧 Profile 的状态也可能被遗留修改。

修复：移除库存 CRUD/CSV 对 Profile/Source 状态的隐式写入。状态边界见 [DEVICE_INVENTORY_COLLECTION_BOUNDARY.md](DEVICE_INVENTORY_COLLECTION_BOUNDARY.md)。

回归覆盖：

- `DEVICE_DISABLE` / `DEVICE_REENABLE`
- 手工停用 Profile/Source 后库存编辑不会重新启用
- `PROFILE_REASSIGNMENT`
- 既有 terminal task 的独立 admission policy
- terminal session history 保留

### P1：CSV preview 重复提交与失败重试

原行为：两个请求可以同时读取同一个 preview，随后分别进入 SQLite transaction；更新型批次可能发生最后写入覆盖，失败时 preview 也没有明确的领取状态。

修复：

- preview 绑定创建者；
- `Committing` 在进 transaction 前原子领取；
- transaction 失败后回滚并释放 token，允许安全重试；
- 成功后保存有界、短期 commit receipt，客户端超时重试返回同一结果；
- 不同管理员不能消费他人的 token。

### P1：CSV 严格性与 preview TOCTOU

原行为：重复 Header 会被 Dictionary 覆盖，缺失/多余字段不会拒绝，重复 device ID 直到 commit 才触发数据库错误；preview 后目标设备被修改时，更新仍可能无条件覆盖。

修复：

- 拒绝重复/空/未知 Header；
- 每行字段数必须与 Header 完全一致；
- 支持 BOM、CRLF/LF、引号转义和 quoted field；拒绝非法闭引号、未闭合引号和超长字段；
- 拒绝批次内重复 device ID、重复 Profile、ID/IP 错配和公式样式值；
- update candidate 保存 `updated_ms`，commit 使用乐观并发条件；
- commit 仍使用单 SQLite transaction，任何冲突整体回滚。

### P1：legacy bridge 重复库存与 GET 隐式写入

原行为：`QueryManagedDeviceInventory` / detail 查询调用 `EnsureInventoryBridgeRows`，普通 GET 可能写库；v11 bridge 和旧 Profile API 对同一站点/IP 的多个 Profile 没有去重边界。

修复：

- GET 查询不再创建库存行；
- v11 migration 使用站点/IP 分组选择一个 bridge candidate，并跳过已有站点/IP；
- legacy Profile API 创建 bridge 时也跳过已有站点/IP；
- 旧 H3C Profile 本身保留，未删除历史 terminal/session/Raw/Catalog。

## 未发现阻断问题

- Auth：设备写接口仍要求 `SUPERADMIN`；匿名请求被全局会话层拒绝。
- CSRF：Cookie 会话写请求仍要求同源 Origin、CSRF Header/Cookie 和服务端 token 校验；Bearer local API token 继续使用既有本地边界。
- 审计：成功的设备 CRUD、分组、CSV create/update/commit 记录 actor、subject、动作和非敏感字段；未写入密码、Token、私钥或凭据密文。
- Schema：v10/v11 初始化脚本可重复执行，外键仍由启动阶段检查；v11 migration 保留旧 Profile 和 terminal profile ID。
- 身份：设备 ID 不随管理 IP 修改；IP 作用域为 `site_id`；Profile 关系保持一对一。
- Raw/Catalog、G1、G2.3/G2.4、PR2 ZIP、Desktop Helper 未改变。

## 非阻断 P2 / 后续边界

- 失败的设备写请求目前不新增持久化 device audit failure event；HTTP 状态和既有服务日志提供诊断。Phase 2 应补充安全审计事件模型，避免把拒绝、冲突和失败操作只留在日志中。
- preview 是进程内状态；服务重启后 token 安全失效，不尝试恢复未提交 CSV。Phase 2 如需跨重启恢复，应新增受控 staging 表和过期清理，而不是复制原始 CSV 到长期目录。
- 站点/IP 的 API 写入采用 Serializable transaction 与应用校验；未来若允许更多直接数据维护入口，再评估带历史重复数据 remediation 的数据库唯一索引迁移。
- 本轮未执行真实设备、Windows 服务安装、生产/开发 Authority 或正式部署验收。

## 测试证据

- `dotnet build tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore`：PASS；保留既有 `NU1903` warning。
- `dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-build`：PASS。
- 新增隔离回归：状态解耦、Profile 重关联、legacy bridge 去重、CSV 严格 parser、资源上限、stale preview、失败重试、幂等 receipt、Profile 一对一。
- 数据仅使用临时测试根；未访问 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。
