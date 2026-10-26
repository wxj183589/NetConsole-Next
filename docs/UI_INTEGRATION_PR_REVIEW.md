# NetConsole Next UI 集成 PR 审查

审查日期：2026-10-26（Asia/Shanghai）
仓库：`wxj183589/NetConsole-Next`
审查目标：`codex-A/next-ui-integration-20261026`
审查 HEAD：`73b7a592a359cfd8a50a0e4e0b5c188a3901f46d`
审计基线：`audit/p0-g1-stack-integration` @ `d07cdb4caaaf9942f560390279d407627037f613`
NetConsole Next `main`：`9c544d2e4c404015e732fdcfd05a031636ee1db2`

## GitHub 与分支边界

- PR #19 当前仍为 open、未合并，head 为 `d07cdb4c`，base 为 `codex/web-pr2-host-tools-files`。
- 集成分支从 PR #19 最新审计 head 建立；没有把旧版 NetConsole 的 `main` 当作 NetConsole Next 主线。
- UI 来源分支为 `codex-A/next-ui-reference-redesign-20261026@780870ab`，采用选择性移植，不做整分支覆盖。
- 本阶段不修改 `main`、不合并 PR #1–#19、不 force push。

## 代码审查结论

结论：`UI_PR_REVIEW_READY`，未发现需要阻断 Draft PR 的 P0/P1。

### 设备库存查询

- `Catalog.QueryDeviceInventory` 以 `h3c_device_profiles` 为登记事实，以 `terminal_sessions` 按 `target_id` 和 `started_ms` 取最近会话状态。
- 统计查询和分页查询复用同一过滤条件，`COUNT(*)` 与返回记录口径一致。
- `page`、`pageSize` 有界；列表使用 `updated_ms DESC, id ASC` 稳定排序，避免翻页时重复或遗漏。
- 搜索值、分页值均为 SQLite 参数；状态过滤来自固定枚举，不拼接用户输入，未发现 SQL 注入路径。
- 空库、缺失资料和空字段会返回空列表或质量问题，不生成虚假设备、在线状态或凭据内容。
- `CONNECTED` 仅表示最近持久化终端会话状态；不是实时探测，也不等同于设备启用状态。前端已显示该语义。
- 当前 API 是 H3C Terminal Profile 的安全投影，不是完整 Device Inventory CRUD；名称、厂商、型号、分组和操作审计属于 Phase 1，不在本 UI 集成 PR 中伪造实现。

### 数据与安全边界

- `RawSegmentWriter`、Raw recovery、Catalog seal/register/conflict 逻辑未被 UI 代码覆盖或替换。
- G1 recovery、G2.3 terminal session/recording、G2.4 Host Key 策略、PR-2 archive queue/disk admission/download lease、Desktop Helper 均保留审计基线。
- `/api/system/devices` 继续经过现有 Web session/bearer 认证中间件；写请求的 CSRF/Origin 检查未被放宽。
- 终端入口仍使用稳定 `deviceId` 和服务端 Terminal Gateway；UI 不接受任意 host/port，也不绕过 Host Key 校验。
- 凭据仅显示“服务端引用/已配置”状态，未进入设备列表、截图、审计文档或浏览器输出。

### UI 与验收证据

- 运行总览、设备管理、三栏终端、主题和响应式布局来自 `780870ab`，集成后重新构建 `wwwroot`。
- `web/artifacts/ui-acceptance/browser-evidence.json` 记录同一集成服务的 Chrome 路径、3 个视口、11 张截图、设备行数和错误计数。
- `consoleErrors`、`pageErrors`、`failedRequests`、`badResponses`、`authOrMissingResponses` 均为空；空 Catalog 的 `deviceRows=0` 与数据事实一致。
- 真实设备、现场 COM、LAN/TLS、782 条历史数据来源和轨交业务验收仍为未完成事项，不由截图或模拟器结果替代。

## 阶段 A 验收门槛

Draft PR 创建前必须通过：

- UI build/typecheck/Vitest 和 .NET Release build。
- C# harness、Raw seal/Catalog conflict、G1 recovery、Auth/CSRF、PR-2 archive。
- SSH/Telnet/COM/split/history/replay/export 浏览器流程及终端契约。
- GitHub Ubuntu/Windows CI 与 PowerShell 脚本检查。
- Windows Chrome 1920x1080、1440x900、1366x768 真实浏览器证据。

## 已知限制

- PR #19 及其上游堆叠 PR 仍需按既定顺序由 owner 审查，不能把本 Draft PR 视为 main 发布完成。
- SQLitePCLRaw NU1903、Web 依赖审计/弃用告警和 Vite 大 chunk 仍记录为风险；本 PR 不扩大范围处理。
- Phase 1 设备资料模型、CRUD、分组、CSV 导入、设备操作审计和 Terminal Profile 关联将在独立分支实现。

## 审查状态

`UI_PR_REVIEW_READY`
