# NetConsole Next UI 集成差异审查

审查日期：2026-10-26（Asia/Shanghai）
仓库：`wxj183589/NetConsole-Next`
集成分支：`codex-A/next-ui-integration-20261026`
审计基线：`audit/p0-g1-stack-integration` @ `d07cdb4caaaf9942f560390279d407627037f613`
UI 来源：`codex-A/next-ui-reference-redesign-20261026` @ `780870abe3e7b2c8430359388458b2fd055bd48b`

## 集成边界

本次集成以 PR #19 当前 head 对应的审计分支为唯一基线。审计分支没有漂移；GitHub PR #19 仍指向 `d07cdb4c`。UI 分支与基线的共同祖先为 `293abdf4fbbfb47af4827d6445624f9fe531cbe8`，相对共同祖先的提交数量为审计分支 34、UI 分支 4，因此不能把 UI 分支当作可直接 fast-forward 的后代。

采用的方式是：先在隔离 worktree 中从审计基线建立分支，再选择性带入 UI 提交的前端、设备库存 API、验收脚本和截图证据；最后重新构建 `wwwroot`。没有把 UI 分支整体 merge 到审计基线，也没有修改 `main`。

## 逐文件审查结论

### 保留的 UI 能力

- `web/src/components/AppShell.vue`、`NcLayout.vue`、`web/src/styles/theme.css`：参考图风格的工作台布局、导航、状态栏、响应式层和深色主题。
- `web/src/views/DashboardView.vue`、`DevicesView.vue`、`TerminalView.vue`：运行总览、服务端设备分页库存、受控 SSH/Telnet/COM 终端和历史回放入口。
- `web/src/api/client.ts`、`web/src/deviceViewModel.ts`：设备库存契约、状态/字段兜底和管理地址格式化。
- `tests/web_terminal_browser_e2e.mjs`、`tests/ui_reference_browser_e2e.mjs`、`tests/web_ui_contract.py`：新 Vue 选择器、真实浏览器终端路径、截图和静态契约验收。
- `docs/UI_REFERENCE_REDESIGN.md`、`docs/UI_DEVICE_DATA_DIAGNOSIS.md`、`docs/UI_VISUAL_ACCEPTANCE.md`、`design-qa.md` 及 `web/artifacts/ui-acceptance/`：视觉验收和设备数据口径证据。

### 后端选择性移植

`Catalog.cs` 只新增 `DeviceInventorySummary`、`DeviceInventoryItem`、`DeviceInventoryPage` 和 `QueryDeviceInventory`。查询以 `h3c_device_profiles` 为登记事实，以最新 `terminal_sessions` 为在线/离线/未知状态来源，并保留分页、异常记录和质量问题；没有替换 Raw recovery、Raw seal、Catalog registration 或历史会话实现。

`Program.cs` 只新增受现有 `/api/system` 认证中间件保护的 `GET /api/system/devices`，并把资源快照明确映射为前端需要的字段。现有 health、terminal、H3C、文件归档、Host Tool、Auth/CSRF 路由保持审计基线语义。

服务测试 harness 只新增设备库存映射、统计、筛选和分页断言；审计基线已有的 G1/G2.3/G2.4、Raw、ZIP、Auth/CSRF 和 Windows 清理测试未删除。

### 静态资产

没有复用 UI 分支的过期 `wwwroot` 哈希集合。前端依赖安装和 Vite build 在集成 worktree 内重新执行，`index.html` 只指向本次构建生成的入口资产；基线仍保留的 legacy terminal 资产继续保留用于回滚/回归契约。

## 语义冲突与风险

当前未发现需要人工裁决的 Git 冲突。唯一需要语义审查的交汇点是 `Catalog.cs`/`Program.cs`：已采取“保留审计基线，再加库存能力”的方式，未使用 UI 分支文件整体覆盖。

已知但不在本次 UI 集成范围内的风险：SQLitePCLRaw.lib.e_sqlite3 2.1.11 仍产生 NU1903；Web 依赖安装仍报告 glob 弃用/审计告警；真实设备、真实 COM、LAN/TLS 和真实 Windows GUI/Explorer 仍需独立验收。它们不应被截图或模拟器 E2E 结果掩盖。

## 回退方案

回退只需移除集成分支中的 UI/设备库存提交，保留 `d07cdb4c` 审计基线；不删除、不复制、不改写 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。本次验证使用独立数据根，不产生生产数据变更。

## 结论

UI 与设备库存能力已按审计基线做选择性集成，Raw/Catalog/G1/G2.3/G2.4/Auth/CSRF/PR2/ZIP/Desktop Helper 的上游实现仍由基线提供。集成分支必须完成服务、前端、终端浏览器、Raw/ZIP/Auth 回归后，才可作为 Draft PR 候选；本文件不表示已合并到 `main`。
