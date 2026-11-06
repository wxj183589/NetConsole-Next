# 任务诊断 TXT / ZIP 下载验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。源码以本文件所属功能提交为准；保留当前分支，仅本地提交，不 Push、Merge、Release。整体 `PARTIAL / REAL_DEVICE_PENDING`。

## 行为

- 任务命令明细支持单条 TXT 和诊断 ZIP。下载只读取已有封存证据，不重新登录设备、不创建采集任务，不增加 schema 或导出文件缓存。
- 新增 `GET /api/system/tasks/{runId}/diagnostics` 和 `/diagnostics/download`；后者可使用从 0 开始的 `commandIndex`。ZIP 含 `manifest.json` 和符合条件的 `command-NNN.txt`；TXT 为脱敏后的 UTF-8 回显，无额外标题。
- 只允许明确列出的 13 条 H3C 诊断命令；`display current-configuration`、mesh-profile、service-template 配置类输出排除。失败/取消任务可下载已有封存回显，manifest 明确 PARTIAL，不将 partial 当作成功采集。
- 核对封存 segment SHA、output SHA、字节数、严格 UTF-8、Raw 类型、source/run/profile/host、命令顺序/索引、捕获时间及计划。路径越界、Reparse Point、缺文件、篡改或超限拒绝下载。单条脱敏前后均不超过 256 KiB，包上限 8 MiB + 128 KiB。
- 防御脱敏 secret 行和私钥；manifest 分别记录 Raw 与实际下载字节的 hash/size，不输出路径、密码、Profile 标识或 parsedJson。
- 使用当前登录会话、角色、Site 及冻结/当前设备归属；发送前和每 64 KiB 重新核验，撤权或取消后停止发送。响应 attachment / no-store / nosniff。
- 前端依据 manifest 提供下载；Blob 接收后再次读取资格，关闭明细、切任务、路由/身份变化及页面退役会取消迟到请求并清空旧数据，ObjectURL 使用后释放。

## 已执行验证

| 检查 | 结果与范围 |
| --- | --- |
| Release Service / Tests build | PASS，0 warnings / 0 errors |
| 默认完整 Service harness | 最终大小边界修复后 PASS，EXIT 0，session `60396`；含诊断89/89、VLAN6/6、publication14/14及既有权限/连接/并发/迁移/恢复回归 |
| 后端定向 harness | `--task-diagnostic-evidence-tests`：89/89 PASS；单/多命令及历史 ledger、23 组证据篡改、partial/missing/config 排除、大小边界、Site/endpoint 重绑、logout/expiry/disable/role、64 KiB 后撤权、取消、并发读取及零新增任务/文件 |
| HTTP → Netmiko → 合成 SSH | `py -3.13 tests/task_diagnostics_e2e.py`：21 checks PASS；TXT/ZIP/hash/配置排除、匿名/跨 Site/无 membership、篡改、重启后无新设备命令、退出会话和资源释放 |
| Vue | `pnpm test`：14 files / 124 tests PASS，含 TasksView 20/20；`pnpm typecheck`、`pnpm build` PASS；Vite 既有大 chunk 提示保留 |
| Chrome 源码 UI / 模拟 API | `node tests/task_diagnostics_browser_e2e.mjs`：8/8 PASS；真实浏览器 TXT/ZIP 下载、状态/权限/迟到隔离、1920×1080 与 2560×1440；4 张截图已检查 |

浏览器证据：`D:\study\NetConsole-Next-Workspace\build\task-diagnostics-browser\acceptance.json`，类型 `CHROME_SOURCE_UI_MOCKED_API_FIXTURE`，不表示后端或真实设备验证。实际 HTTP 测试为独立 Service + CPython/Netmiko + 合成回环 SSH，流中撤权与取消由确定性后端 harness 覆盖。HTTP 测试在最后新增的脱敏后大小限制前运行；该限制由后续完整 harness 覆盖。准确 SHA 离线候选结果由集成检查点补充。Workspace Python4 tests / JS路径检查、`git diff --check` PASS。

## 数据与边界

仅 Next Workspace 隔离 fixture / SQLite / 回环 SSH；自有测试 Service/SSH/Chrome/Vite 已退出，临时数据按测试生命周期清理，用户预览 PID 90348 保留。未访问或复制旧 NetConsole 数据根、未执行设备配置变更。本功能不是配置备份下载，也未补齐原版全部 AC 命令、多厂商结构化业务、通用断线自动恢复或现场设备兼容。
