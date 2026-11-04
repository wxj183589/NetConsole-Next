# GUI验收

证据类型：OFFLINE_FIXTURE_EVIDENCE。实际Chrome打开独立Vite 18792，通过Playwright拦截API；后端projection响应来自本轮.NET fixture序列化，105行用于分页边界。截图不可作为真实模拟器在线证据。

| PAGE | ROUTE | STATE | VIEWPORT | DPR | SCREENSHOT | INTERACTION | RESULT |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 无线业务 | /wireless | normal | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-1920.png | Domain、双 AC、多来源、分页显示 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-2560.png | 辅助视口 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-ac1.png | AC 筛选同时限制组与详情 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-ac2.png | AC2 独立来源 | PASS |
| 无线业务 | /wireless | conflict | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-conflict.png | 多来源状态冲突提示 | PASS |
| 无线业务 | /wireless | group-error | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-group-error.png | 采集/API 失败可见，不伪装空组 | PASS |
| 无线业务 | /wireless | denied | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-denied.png | 撤销后不残留旧数据 | PASS |
| 无线业务 | /wireless | revision-drift | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-revision-drift.png | 分页间revision变化时拒绝拼接 | PASS |
| 无线业务 | /wireless | empty | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-empty.png | 空态 | PASS |

已查看主1920/辅助2560实际截图，页面保留演示标记；测试搜索第105行、无匹配详情清空、AC组/来源隔离、状态筛选、多个master冲突提示、503/403、分页revision变化拒绝拼接及空态。pageerror=0；预期注入503/403会产生浏览器资源错误。

工作台、设备管理、终端、任务的Service-backed GUI未重跑（B02）；不得宣称整产品GUI完成。原生125%仍未通过。
