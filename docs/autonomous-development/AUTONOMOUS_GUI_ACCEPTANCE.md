# GUI增量验收（Checkpoint J）

证据类型OFFLINE_FIXTURE_EVIDENCE：实际Chrome访问独立Vite 18792，由Playwright拦截/api/，后端无线/runtime数据来自本轮.NET fixture序列化。105行分页数据、52样本历史fixture。15种状态及两个详情截图；不是模拟器实时/Service-backed HTTP证据。

| PAGE | ROUTE | STATE | VIEWPORT | DPR | SCREENSHOT | INTERACTION | RESULT |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 无线业务 | /wireless | normal | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-1920.png | Domain、双 AC、多来源、分页显示 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-2560.png | 辅助视口 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-ac1.png | AC 筛选同时限制组与详情 | PASS |
| 无线业务 | /wireless | normal | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-ac2.png | AC2 独立来源 | PASS |
| 无线业务 | /wireless | conflict | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-conflict.png | 多来源状态冲突提示 | PASS |
| 无线业务 | /wireless | runtime-error | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-runtime-error.png | 历史失败不保留旧来源状态 | PASS |
| 无线业务 | /wireless | partial-history | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-partial-history.png | 扫描上限或缺证据不虚报完整Recent10 | PASS |
| 无线业务 | /wireless | group-error | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-group-error.png | 采集/API 失败可见，不伪装空组 | PASS |
| 无线业务 | /wireless | denied | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-denied.png | 撤销后不残留旧数据 | PASS |
| 无线业务 | /wireless | revision-drift | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-revision-drift.png | 分页间revision变化时拒绝拼接 | PASS |
| 无线业务 | /wireless | empty | 2560x1440 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\wireless-empty.png | 空态 | PASS |
| 文件管理 | /files | file-scoped-user | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\files-scoped-user.png | Site用户不请求或显示跨Site文件元数据 | PASS |
| 文件管理 | /files | normal | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\files-global-user.png | 当前全局管理员保留逻辑目录与列表 | PASS |
| 文件管理 | /files | file-denied | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\files-role-revoked.png | 403后清空旧目录、文件和任务状态 | PASS |
| 文件管理 | /files | file-session-expired | 1920x1080 | 1 | D:\study\NetConsole-Next-Workspace\build\autonomous-20261105\gui\files-session-expired.png | 401后清除旧元数据和本地认证状态 | PASS |

已视觉核验主1920/辅助2560、runtime详情和文件拒绝/全局列表截图。自动断言包含历史读取失败清空、partial history提示、Recent10/来源筛选、AP组与详情同步、搜索第105行、状态冲突、权限撤销、revision变化拒绝拼接；文件部分包含Site初始拒绝且不请求roots、全局管理员正向列表、401/403清空、晚到旧200不恢复元数据与sessionStorage。

page errors/unexpected requests/unexpected routes均为空。预期注入503/403/401的浏览器资源报错有记录，不冒充意外故障。工作台/设备/终端/任务新代码Service-backed GUI未重跑，受B02阻断；原生125%仍无证据，不宣称整产品GUI完成。
