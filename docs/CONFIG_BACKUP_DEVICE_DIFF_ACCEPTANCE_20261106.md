# 设备配置备份入口与有界差异验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。基线：`764d764`；功能提交以本文件所属 commit 为准。整体产品仍为 `PARTIAL`。仅本地提交，不 Push、Merge、Release。

## 行为

- 设备详情通过库存 UUID 打开当前设备的配置备份，后端关联的 Profile ID 是唯一连接依据。进入页面只加载历史，用户明确点击才开始采集；不按名称、IP 或列表第一条猜测关联。
- 当前只支持独立管理的 H3C 设备。模块未启用、关联丢失、Site 不可见或厂商不支持时给出中文原因；FIT-AP 继续通过 AC 管理。后端在创建整个批次前检查全部目标，并在创建子任务前复核；不支持的目标返回 `409 CONFIG_BACKUP_TARGET_UNSUPPORTED`，隐藏目标先返回 404。
- ADMIN 可读取当前授权历史和差异，备份写入仍限 SUPERADMIN。切换设备、页面停用、撤权和迟到响应不会回灌上一设备的历史、批次或差异。五分钟轮询窗口结束但任务仍运行时显示“仍在运行”，不将其宣称完成。
- 普通设备连接继续使用统一 Netmiko worker。备份复用现有任务、Raw、敏感行脱敏、Site claims、版本文件和下载流程，不新增连接或凭据体系。

## 差异语义与边界

旧集合比较会丢失行顺序及重复次数，且只读取前 16,384 行。本批按完整行序列比较既有上限 256 KiB 的配置文件，保留顺序和重复行。与原行读取语义一致，不区分换行编码和末尾换行差异。

先去除共同前后缀，再对最多 1,048,576 单元的中段使用 LCS；超过预算时保守地将中段计为删除和新增。因此大差异不会漏报，但不保证最短编辑。`AddedLines` / `RemovedLines` 是完整编辑计数；`ChangedLines=min(AddedLines, RemovedLines)` 表示配对替换，不另加总。每侧预览最多 256 行，UI 对截断显示提示，重复文本使用独立渲染 key。

## 验证

- 差异修复前新增回归复现六项缺陷；修复后配置备份定向 harness PASS，覆盖重排、重复次数、等长替换、16,384 行后变化及 12,000×12,000 大输入的计数/预览和分配预算。后者限制差异调用分配小于 32 MiB，防止无界二维表。
- 厂商和 FIT-AP 门禁直接从服务测试调用，检查拒绝混合批次时没有创建任何任务。正常备份、脱敏、版本、下载和 Site scope 既有回归保留。
- Release Service/Tests 构建 0 warning / 0 error；默认完整 Service harness exit 0 PASS，含 AP Group、缓存、F10 更换证据、运行态、Raw 发布、并发、迁移与崩溃恢复。配置备份使用独立入口，单独记录 `CONFIG_BACKUP_TESTS=6/6 PASS`。
- Web 全量 Vitest 11 files / 65 tests PASS，`pnpm typecheck` PASS。
- `py -3.13 -B tests/config_backup_e2e.py` exit 0：`CONFIG_BACKUP_HTTP_NETMIKO_E2E=PASS (19 checks)`。真实 HTTP Service → CPython/Netmiko → 回环合成 SSH，四批次五个快照，覆盖双目标显式采集、相同备份、重复次数、行重排、版本顺序、脱敏下载/hash、ADMIN 拒绝写入及授权读取、跨 Site 隔离、不支持目标拒绝且零命令/零任务。测试服务、SSH 会话和临时数据均已退役。
- HTTP SUPERADMIN 始终为全局 scope，不能用它制造“非全局 SUPERADMIN”场景。HTTP 验证缺失目标 404 和 ADMIN 隐藏历史/下载；隐藏且不支持的目标先 404、后 409 的边界由直接 Service scope 回归验证。
- Chrome 源码 UI + API mock：六场景 PASS，1920×1080 DPR1；精确设备入口、明确开始、无效链接/厂商/模块拒绝、ADMIN 只读和撤权清空。无页面错误或未知 API；该浏览器证据不表示实际设备采集。

证据：Workspace `build/device-backup-browser/acceptance.json` 和同目录页面截图。主要文件：`ConfigBackupService.cs`、`ConfigBackupTests.cs`、两个设备/备份 View 与组件测试、`tests/device_backup_browser_e2e.mjs`、`tests/config_backup_e2e.py`。

仅使用 Next Workspace 内的隔离 fixture；未访问旧 NetConsole 数据根，未执行设备配置变更。读取配置用于备份，不等于写入设备配置。现场 H3C、其他型号和实际业务兼容仍 `REAL_DEVICE_PENDING`。
