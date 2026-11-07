# R7.1 配置管理：完整原件批量、范围选择、持久计划与重新连接验证

日期：2026-11-08。状态：**PARTIAL**。本轮闭环完整私有原件的多设备批量与持久计划产品流程；原生整机恢复仍为 **LIVE BLOCKED**。配置原件备份成功不代表整机恢复成功。

## 产品实现

- 新增 `COMPLETE_RUNNING` 批量类型，调用既有完整原件采集 Service。私有 `.cfg` 与公开脱敏 `.view.cfg` 分开保存；任务 Raw、公开 Facts、页面和下载不承载私有原件。普通脱敏快照列表和 manifest 验证不接受此私有类型。
- 设备选择、精确 Site 和 AC Domain 成员均先解析为最多 32 个独立管理设备档案，展示确认预览，再提交固定集合；不通过地址模糊匹配、不跨 Site 猜测关联。FIT_AP 与未授权端点硬阻断。每批最多并发 4 台，沿用档案锁、权限、状态与失败项结果。
- 提交前检查全部目标；任务入队时固定 actor / Device / Site / endpoint。排队后端点或授权变化不能把任务静默重定向到新目标；发布前再次验证 scope，撤权后不发布文件。
- 完整批量结果指向私有完整备份版本，可按档案进入完整历史；仅失败设备重试保留其它成功版本。范围输入变化撤销旧预览，过期异步响应不重启有效选择。进度轮询在 KeepAlive 页面停用时暂停，手动刷新不会产生多条轮询链。
- 持久计划沿用 `backup/config-schedules.json`，支持完整原件类型。创建时固定档案集合，之后新增 Site / Domain 成员不自动加入；每次到期重新检查当前所有者、凭据、scope 和白名单，再固定本次批量执行身份。同一计划不重叠、不补跑停机周期；重启保留计划并将未完成执行标记中断。创建、启用和到期均执行完整类型专用目标预检。
- 计划最短周期为 60 分钟；页面支持创建、暂停、启用、删除、刷新和立即执行。普通脱敏计划组件过滤私有类型，避免混淆。第二批补齐固定档案集合明细及逐档案历史入口，展示最近开始 / 完成时间；“查看最近执行”复用既有 LastBatchId 只读查询，直接显示成功 / 失败明细并可仅重试失败项。记录过期或权限变化时显示安全错误，不自动再采集。
- 第四批新增产品 `POST /api/system/config-backup-schedules/{id}/run` 和计划“立即执行”：复用定时触发的持久准入、当前所有者 / scope / 凭据 / 模块 / 目标验证及批次完成观察。允许暂停计划明确执行一次，保持原启停状态和正常到期时间；手动 / 自动共用同计划防重叠与全局 4 计划并发限制。前端直接打开返回的真实 LastBatchId。
- 描述 Writer 在写连接关闭后建立独立新连接回读，验证完整配置除预期描述外不变且型号 / 软件版本匹配；失败恢复也关闭写连接，再建新连接验证完整恢复和版本。没有增加写命令，也没有扩展为整机恢复。
- 完整历史的“导出脱敏比较报告”产品按钮继续导出经后端脱敏的 JSON，包含前后身份、哈希、增删数量、截断标记和隐藏敏感变化提示；不导出私有原件，不引入 CSV 公式表面。

## 真实 HTTP 只读采集证据

运行脚本：`tests/r71_complete_configuration_http.py`。使用 Next 隔离 runtime `test-data/simulator-live-20261105` 的既有本机 API token；设备凭据仅由现有服务端读取，脚本不读取或输出设备凭据。设备窗口由 device workflow 协调，严格单目标逐台等待终态。

报告：`D:\study\NetConsole-Next-Workspace\reports\r71-complete-configuration-20261108-005555.json`。

| 设备 | HTTP COMPLETE_RUNNING batch | 私有完整版本 |
| --- | --- | --- |
| AC1 | SUCCEEDED | bbff888ecca9436db5b67a5f9ef37925 |
| AC2 | SUCCEEDED | 12cd7d170e1a4bf894bce82da8a06a9f |
| SW | SUCCEEDED | af9aac7ef1824d109a86ea56afe9a940 |

三台均逐项验证私有原件和脱敏文件的字节数 / SHA256，以及 HTTP view 返回内容的脱敏哈希。AC 使用精确 `r68-simulator` Site，SW 使用精确 `local` Site；Site 预览覆盖既定档案，未将两组 Site 合并猜测。

通过真实 API 创建三档案 `COMPLETE_RUNNING` 60 分钟计划，并验证持久文件中的类型和档案集合，随后暂停并删除验收计划。**未等待 60 分钟实际到期，未人为调整 due 时间，未重启服务**；实际调度执行和重启行为由隔离 fixture 验证，本次 live 证据只覆盖计划准入、持久化及暂停 / 删除。

首次脚本将 SW 假定为 AC 同 Site，在预检阶段失败；第二次移除 Site 条件暴露 AC 多 Site 档案歧义，仍在设备连接前失败。修正为上述精确 Site 后通过，未为消除歧义修改库存。前两份失败报告保留为 `reports/r71-complete-configuration-20261108-005457.json` 和 `...005534.json`。本次没有配置写入、HA 切换或整机恢复尝试。

## 定向验证

- 独立 Release Service harness build：0 warnings / 0 errors。
- `--complete-config-batch-tests`：**11/11 PASS**。覆盖完整私有原件与脱敏分离、部分认证失败及仅失败项重试、全目标预检、排队后端点变更、发布前撤权、持久计划重启 / 到期执行、不重叠、精确 Site / Domain、跨 Site 拒绝、所有者权限和白名单撤销。
- `--simulator-config-tests`：**19/19 PASS**。覆盖新连接完整回读、回读连接失败后独立恢复、型号版本漂移拒绝和回滚版本漂移拒绝，验证重连过程不遗留同时存活的连接。
- `--config-backup-schedule-tests`：**19/19 PASS**，新增手动执行保持周期 / 暂停状态、手动与自动共享并发 / 防重叠、当前权限 / 目标 / 模块 / 持久化失败阻断。
- Web 3 个测试文件：**34/34 PASS**（CompleteConfigurationBulk 14、ConfigBackupSchedules 8、SimulatorConfiguration 12）。覆盖预览、精确选择、过期响应、失败重试、计划生命周期、安全错误、部分加载失败、手动刷新并发和 KeepAlive 暂停恢复；新增计划最近执行只读查询 / 错误、固定档案历史入口、JSON 比较下载内容与文件名验证。
- `vue-tsc --noEmit`：PASS。

测试使用 Next Workspace 内最小隔离 fixture；fixture 不代表真实 SSH / Telnet / 恢复互操作。完整 Service harness、最终静态构建、浏览器整体验收和提交由根集成统一执行。

## 仍然阻断的能力与数据边界

第二批只读审查原版 `D:\study\NetConsole-Workspace\NetConsole` 源码，未访问其运行数据：`src/netconsole/services/config_collection_web_service.py`、`config_collection_job_handlers.py`、`backend/api/config_collection_router.py` 及 renderer `api/configCollection.ts` 提供配置采集、内容、历史差异、导出和固定 `save force` 的预览 / 确认。未发现设备完整文件上传、staging 或 native replace 的实际恢复实现。`ground_unattended/task_scheduler.py` 的 `CONFIG_RESTORE` 仅为任务类型枚举，不能代表执行实现；`job_center/handlers/database_jobs.py` 的 `database_backup_restore` 调用的是本地 SQLite 数据库恢复。`docs/storage/BACKUP_AND_RESTORE.md` 说明局点迁移包 / 数据库恢复，均不能当成网络设备整机恢复。

Next 的 `tools/netmiko_worker.py` 仅实现 SW 受控 `configuration commit`，没有已验证的 replace 交互或完整配置传输操作；`ISimulatorConfigurationSession.SupportsNativeCheckpointRestore` 在真实 transport 仍为 false。因而目前缺口同时包括：能够实际生成并枚举唯一新检查点的设备、已验证的 replace 确认 / 完成提示符、任意历史原件的有界传输 / staging 与清理，以及上述路径后的完整重新连接和业务探针真实验收。没有可直接迁移的原版整机恢复代码。

真实 SW 的 `configuration commit` 未产生可读取检查点，既有真实 HTTP 证据为 `reports/r70-configuration-http-20261108-000030.json`、`NATIVE_CHECKPOINT_NOT_CREATED`。真实 session 的 native replace 交互能力仍关闭。没有可验证检查点时不执行描述变更或 replace；本轮没有绕过此阻断。

任意历史完整配置的全量传输 / staging / replace 和真实整机恢复仍待具备可验证能力的授权设备。不通过逐行合并或描述恢复冒充整机恢复；fixture 中的 rollback 验证也不能标记 live 整机恢复通过。

未读取或写入旧 NetConsole 的 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未复制真实数据根。真实读操作只产生 Next 隔离 runtime 的新完整备份及元数据；前三批验收计划已删除，第四批保留一份已暂停 SW 浏览器计划及一份待自然到期 AC1 计划，备份作为证据保留。未推送、未合并、未发布；由根集成统一管理 Git 与 DevStatus。

## 自然到期与立即执行补充验收

2026-11-08 01:16:40 通过现有产品 API 创建独立 task-owned 单 AC1 私有完整计划 `6334c82406a348689bf29dd186137351`，Owner 为 `local-api-token`，固定档案 `b9feedeb32e54ed0a99c113cf84263d6`。60 分钟自然到期时间为本地 02:16:40.761（UTC 2026-11-07 18:16:40.761，`1794075400761`）；未修改 due 文件或模拟时钟。创建报告为 `reports/r71-natural-due-plan-20261108-011640.json`。此计划保持启用，由根集成在受控停服 / 重启后观察真实自然到期，完成后暂停；创建不代表到期执行已经通过。

新增 `tests/r71_schedule_run_http.py`，用浏览器同一 SUPERADMIN 所有者通过产品 API 创建单 SW 计划，检查缺少 CSRF 拒绝、立即执行保留周期、真实只读完整备份哈希、后台完成观察和暂停保留最近批次。该 HTTP 脚本仅完成语法校验，真实设备采集由下述浏览器脚本覆盖，未为重复验证额外执行一次备份。

### 真实浏览器 Run-now 闭环：PASS

`tests/r71_schedule_browser_acceptance.mjs` 在根集成完成受控重启、Service 健康后执行。报告为 `reports/r71-schedule-browser-2026-11-07T17-26-09-781Z/result.json`，同目录保留 `schedule-recent-1920.png` 和 `schedule-recent-2560.png`；已人工查看 1920 截图，表格、计划状态、最近批次与成功项可见。两宽度 document.scrollWidth 分别为 1920 / 2560，无页面横向溢出，pageErrors 为空。

- 使用真实 SUPERADMIN 浏览器登录，选择精确档案 `c18dcb225dda4d059ce9799a71a28b2b`；预览确认仅授权 SW 一台，通过 UI 创建 60 分钟 `COMPLETE_RUNNING` 计划 `dcc9d0c3cfd74ed69703661657fe31df`。
- UI 点击“立即执行”返回 202，启用状态和下次自动时间保持不变；真实批次 `d88f1bd72d724596bbfd997bbbb72950` 为 `SUCCEEDED`，成功 1 / 失败 0。
- 完整版本 `dee5c375cbe841d4898e7bf05b904343` 的私有 `.cfg` 和脱敏 `.view.cfg` 均逐一验证字节数和 SHA256。报告不保存配置正文或凭据。
- 等待正常后台 tick 持久化 `SUCCEEDED` 与真实 LastBatchId，UI 暂停后 enabled=false / nextRunMs=null；“查看最近执行”打开上述同一批次，展示成功项与完整历史入口。计划以暂停状态保留供后续人工浏览器复验。
- 额外真实 HTTP 验证同计划缺失 CSRF 返回 403，另一个合法 SUPERADMIN 身份 `local-api-token` 返回 404，未建立设备连接；报告为 `reports/r71-schedule-run-authority-20261108-012727.json`。

首次浏览器脚本按地址定位暴露了 SW 存在多条库存档案，在设备连接前安全断言失败。脚本改用精确 checkbox profile value，并在路由层阻断任何不属于本脚本新计划的 run 请求；未修改库存或猜测 Site。失败报告保留在 `reports/r71-schedule-browser-2026-11-07T17-25-36-394Z`。

自然到期 AC1 计划 `6334c82406a348689bf29dd186137351` 在重启后经产品 GET 只读复核，NextRunMs 仍为 `1794075400761`，enabled=true / NEVER_RUN。报告为 `reports/r71-natural-due-after-restart-20261108-012655.json`。**这证明真实重启保留精确自然到期时间；仍未证明到期设备执行通过，需根集成在 02:16:40.761 后观察并暂停。** 未更改此计划，也未改时钟或持久文件。


## 第五批：容量准入、版本单调性与运行中删除

- 修复完整备份版本分配依赖正文完整性的问题：只以结构有效的 manifest 计算下一版本，私有或脱敏正文损坏仍占用原版本号；结构无效 manifest 不提供版本。List / View / 配置变更 / 恢复输入继续验证私有字节数、SHA256、完整输出与脱敏对应关系，不把结构有效视为内容可信。同次 Read 复用已校验私有原文，减少重复读取。
- 为专用 backup/config-complete 存储增加非破坏性准入，默认全局 1 GiB、每设备档案 1000 份。连接前检查现有容量，发布前按实际原文、脱敏和 manifest 合计字节数再次检查；不同档案共享 publication 锁，防止并发超额。三文件沿用 manifest 最后发布，拒绝配额时不写任何新备份文件。已有损坏文件、孤立文件和临时文件也计入空间，遇链接路径拒绝。无自动删除、压缩或迁移。
- 全局字节配额覆盖 config-complete 目录当前文件（包含其操作元数据）；这是完整备份新增发布准入，不是整个 DataRoot 磁盘空间配额。普通脱敏备份、Raw、归档及其它目录不在此配额内，实际磁盘不足仍由既有 IO 失败路径报告。列表 100 条仍只是显示上限。
- 达到上限时返回 CONFIG_COMPLETE_STORAGE_LIMIT / CONFIG_COMPLETE_HISTORY_LIMIT（直接采集 HTTP 507）；批量结果和计划最近错误保留同一错误码，页面说明历史仍可读取并提示联系管理员。已存在或超过新阈值的历史仍能查看、比较和下载，不因降低配额失效。
- 删除运行中计划不释放仍执行批次的并发槽位；观察到真实终态后释放槽位和 retention pin，不重建已删除计划。完成批次仅内存元数据保留最多 128 份，未观察的计划批次暂时 pin；此限制不删除备份文件。

运维设置（重启 Service 生效）：

| 环境变量 | 默认值 | 有效范围 |
| --- | --- | --- |
| NETCONSOLE_NEXT_COMPLETE_CONFIG_MAX_STORED_BYTES | 1073741824（1 GiB） | 1048576..107374182400（1 MiB..100 GiB） |
| NETCONSOLE_NEXT_COMPLETE_CONFIG_MAX_BACKUPS_PER_PROFILE | 1000 | 1..100000 |

无效、越界或非数字值使启动明确失败，避免静默取消配额。达到阈值时先查看计划失败项与磁盘可用量，再由管理员在确认容量后调整环境变量并受控重启；本轮没有提供自动历史删除接口，也没有删除任何既有备份。勿以整根真实数据复制解决容量问题。

新增定向验证：schedule 19/19、simulator 19/19、complete batch 11/11；覆盖删除运行计划的终态回收、最高版本正文损坏仍递增、结构损坏拒绝、连接前与发布前容量边界、孤立文件计额、旧历史不受限、不同设备并发共用配额及环境变量上下界。Web 计划/批量/完整历史 34 项覆盖容量提示保留成功历史。隔离 Release harness build 0 warnings / 0 errors。新增容量代码尚未作为 live 满盘测试执行，避免填满运行数据磁盘；由根集成重启最新 Service 后复验正常路径。
