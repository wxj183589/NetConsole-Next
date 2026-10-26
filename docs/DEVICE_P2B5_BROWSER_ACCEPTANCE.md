# Device Management P2B5 — Browser Acceptance

日期：2026-10-27  
脚本：`tests/p2b5_network_planning_browser_e2e.py`  
浏览器脚本：`tests/p2b5_network_planning_browser_e2e.mjs`

## Gate

```text
P2B5_BROWSER_E2E=PASS
P2B5_UI_ISOLATED_SERVICE=PASS
```

验收在真实编译的 .NET service 和真实 Vite 构建产物上执行。服务以发布目录作为 content root，确认 `/login` 和 `/app/assets/*` 可被真实服务返回；测试 fixture 位于临时目录，结束后由临时目录生命周期回收。

## 已验证路径

- Superadmin 登录并读取 v14 地址、Domain、VLAN、差异和操作 API。
- 新增地址、编辑地址接口并读取 Revision 1 历史。
- 新增 ACCESS VLAN（PVID 与 VLAN ID 一致）。
- 对地址创建审核记录。
- CSV ADDRESS Preview → Dry-Run → Confirm，确认单事务提交。
- 规划 vs 实测和操作历史页面。
- 浅色 1920×1080 / 1680×943，深色 1440×900 / 1366×768。
- Admin 登录后在无可信 Site Claims 时显示只读不可用边界，且没有“新增地址”写入按钮。

## 诊断

证据 JSON：`web/artifacts/p2b5-network-planning-browser/browser-evidence.json`。浏览器证据包含 console/page error、failed request、unexpected HTTP 统计和预期的未登录 401、Admin scope 403。最终统计为：

```text
consoleErrorCount=0
pageErrorCount=0
failedRequestCount=0
unexpectedHttpCount=0
```

预期的 401/403 是登录初始化和 Admin 无 Site Claims 的安全边界响应，不被当成业务成功或意外服务器错误。

## 截图

截图位于 `web/artifacts/p2b5-network-planning-browser/`，包括地址、Domain、VLAN、差异、操作历史、深色响应式和 Admin 无 scope 状态。目录可由上述脚本重新生成，不复制任何真实数据。
