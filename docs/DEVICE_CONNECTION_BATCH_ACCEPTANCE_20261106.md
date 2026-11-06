# 设备列表批量连接测试专项验收（2026-11-06）

状态：`LOCAL_SYNTHETIC_PASS / REAL_DEVICE_PENDING`。

分支：`codex-A/netconsole-next-r3-dual-ac`。本批工作保留在当前分支，未 Push、Merge 或 Release。本记录只覆盖专用批量连接入口。

## 实现与调用链

设备管理页在现有选择栏新增“批量测试连接”，提交 `POST /api/system/devices/connection-tests`，请求为 `{ "deviceIds": ["库存 UUID"] }`。仅提交用户明确选择的 ID，跨页选择保留明确目标，不把分页条件或“全部设备”转换为隐式连接范围。

`DeviceConnectionTestService.Start` 从库存读取精确关联的连接档案，将固定只读 `display version` 交给现有 `H3cBatchTaskService` 和有界调度器。后续调用统一 session / CPython + Netmiko，自动读取已有设备凭据及连接策略；没有新增 SSH/Telnet 客户端、认证流程、连接池或独立调度器。

创建成功返回 201 和现有 `TaskPlanSnapshot`，Location 为 `/api/system/h3c/task-plans/{id}`。读取、报告和取消沿用：

- `GET /api/system/h3c/task-plans/{id}`。
- `GET /api/system/h3c/task-plans/{id}/report`。
- `POST /api/system/h3c/task-plans/{id}/cancel`。

页面展示计划状态、完成进度、每目标库存 UUID/连接档案、子任务 ID、命令状态及失败说明，并提供“刷新结果”“取消测试”“打开任务中心”。结果刷新只读取现有计划，不创建新的连接任务。自动读取间隔为 1.5 秒，最多 200 次，终态停止；需要继续查看时可手动刷新。

## 权限与整批预检

批量入口及已有任务计划的读取、报告、取消均要求 SUPERADMIN。ADMIN 不显示批量按钮，既有单台测试权限保持原有语义。入口要求 `devices`、`h3c` 模块启用且运行配置启用 H3C；不额外要求 `tasks` 模块。

目标限制为 1–64 个显式、不重复、非空的库存 ID。仅接受当前授权 Site 范围内、已启用、精确关联已启用有效连接档案的 H3C 独立设备；拒绝非 H3C、FIT-AP、缺失/禁用档案和身份关联不一致。FIT-AP 继续由 AC 管理。

合法请求先完成整批可见性检查，再校验兼容范围和关联，因此隐藏/不存在目标的 404 优先于不支持目标的 409。上述预检均在创建定义、计划和调度前完成，失败不会为该批创建任务；这不表示运行阶段的多设备命令具有事务性。

入口冻结所选库存 ID 与档案的映射。现有计划服务捕获所有目标的执行 scope 后，在写入任何任务前再次核对 DeviceId；预检与 scope 捕获之间发生解绑、重绑或映射缺失时返回 409。持久化后的身份、端点和当前权限校验继续由既有任务授权管理。

| 情况 | HTTP / 错误码 |
| --- | --- |
| ID 列表为空、超限、重复或格式无效 | 400 / `DEVICE_CONNECTION_TEST_INVALID_TARGETS` |
| 目标不存在或不在当前 Site 授权范围 | 404 / `DEVICE_CONNECTION_TEST_TARGET_NOT_VISIBLE` |
| 目标不支持、禁用或精确关联不可用 | 409 / `DEVICE_CONNECTION_TEST_TARGET_UNSUPPORTED` |
| 角色或 Site 权限不允许 | 403 / `DEVICE_CONNECTION_TEST_PERMISSION_DENIED` |

任务执行和查询沿用已有权限重核。前端在提交、读取和取消前后重新核验必要模块；模块状态不可读或停用时清除计划显示并停止轮询。切换 Site、角色变化、页面卸载或 KeepAlive 停用使旧请求失效，迟到响应不能恢复旧 Site 的结果；切换 Site 同时清空选择。

## 已有测试证据

最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；完整 Service harness 在库存关联 guard、共享预算及终端原因码最终修复后 EXIT 0（session69905）。准确 SHA 离线候选通过当前页面可达、静态 hash 和回环 SSH/Linux 运行门禁，见 [当前候选验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)；运行测试不会扩大下表的浏览器 mock 或合成设备证据范围。

| 验证 | 结果与范围 |
| --- | --- |
| Vue 完整测试 | root 执行：12 files / 82 tests PASS。其中新批量专项 17 tests；批量专项与原设备详情定向运行 23/23 PASS |
| Vue 类型检查 | `npm run typecheck` PASS |
| Chrome 源码页面 | root 执行 `node tests/device_connection_batch_browser_e2e.mjs`：4 组 PASS，页面错误和未处理接口均为空 |
| 后端定向 harness | `dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll --device-connection-tests`：8/8 PASS；精确 ID、404 优先、409 零写入、权限、取消、关联重绑及映射完整性 |
| 实际 Service HTTP | `py -3.13 -B tests/device_connection_tests_e2e.py`：14/14 PASS；真实 HTTP → Service → CPython/Netmiko → 合成 loopback SSH，包含自动凭据/指纹、双目标命令报告、ADMIN 隔离、模块门禁、取消及会话释放；Chrome mock 不计入此项 |

Chrome 4 组覆盖：明确库存 UUID 的创建/刷新/完成及 1920×1080、2560×1440 布局；取消已有计划；Site 切换后丢弃迟到创建响应并清空选择；ADMIN 不显示批量写入口。

浏览器使用真实 Chrome 和源码 Vue/Vite 页面，但所有业务 API 均由 Playwright mock；它验证界面交互和请求契约，不验证实际 Service HTTP、数据库、Netmiko、远程登录或设备行为。证据位于 Workspace `build/device-connection-batch-browser/acceptance.json`、`batch-1920.png` 和 `batch-2560.png`。

Vue 专项另覆盖跨页选择、65 台超限、混合成功/认证失败结果、终态停止轮询、刷新/取消不重复提交、模块停用/不可读、迟到报告、角色变化、KeepAlive 停用和卸载。

## 生产与设备边界

本入口固定执行只读命令，不接收用户命令、凭据或目标地址覆盖。任务中心、Raw、SQLite、业务解析、设备身份和 AC/FIT-AP 关系继续由现有服务管理。没有访问旧 NetConsole 的 Production/Dev 数据根，本前端及文档工作不修改业务数据库。

H3C 独立交换机、路由器和 AC 属于本入口的目标范围，仍须依据具体型号、协议和现场环境验收。驱动存在、计划成功、Fixture 或模拟器通过都不能代替真实设备证据；本批专用入口保留 `REAL_DEVICE_PENDING`。多厂商结构化采集、VLAN CLI 采集及其他既有缺口不因本入口完成而关闭。

SUPERADMIN 的 HTTP scope 为全局；HTTP 检查不存在目标的 404 优先，隐藏目标的 404 优先由显式 Site scope 的服务级测试覆盖。后端测试仅创建 Workspace 内的最小独立 Catalog 与合成 SSH，结束自动回收；没有复制完整 Site 或真实数据根。本次未通过该入口对真实设备执行配置变更，未进行安装、跨机或 Release 验收。
