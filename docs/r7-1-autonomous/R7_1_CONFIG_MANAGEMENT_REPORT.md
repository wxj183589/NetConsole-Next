# R7.1 配置管理：完整原件批量、范围选择、持久计划与重新连接验证

日期：2026-11-08。状态：**PARTIAL**。本轮闭环完整私有原件的多设备批量与持久计划产品流程；原生整机恢复仍为 **LIVE BLOCKED**。配置原件备份成功不代表整机恢复成功。

## 产品实现

- 新增 `COMPLETE_RUNNING` 批量类型，调用既有完整原件采集 Service。私有 `.cfg` 与公开脱敏 `.view.cfg` 分开保存；任务 Raw、公开 Facts、页面和下载不承载私有原件。普通脱敏快照列表和 manifest 验证不接受此私有类型。
- 设备选择、精确 Site 和 AC Domain 成员均先解析为最多 32 个独立管理设备档案，展示确认预览，再提交固定集合；不通过地址模糊匹配、不跨 Site 猜测关联。FIT_AP 与未授权端点硬阻断。每批最多并发 4 台，沿用档案锁、权限、状态与失败项结果。
- 提交前检查全部目标；任务入队时固定 actor / Device / Site / endpoint。排队后端点或授权变化不能把任务静默重定向到新目标；发布前再次验证 scope，撤权后不发布文件。
- 完整批量结果指向私有完整备份版本，可按档案进入完整历史；仅失败设备重试保留其它成功版本。范围输入变化撤销旧预览，过期异步响应不重启有效选择。进度轮询在 KeepAlive 页面停用时暂停，手动刷新不会产生多条轮询链。
- 持久计划沿用 `backup/config-schedules.json`，支持完整原件类型。创建时固定档案集合，之后新增 Site / Domain 成员不自动加入；每次到期重新检查当前所有者、凭据、scope 和白名单，再固定本次批量执行身份。同一计划不重叠、不补跑停机周期；重启保留计划并将未完成执行标记中断。创建、启用和到期均执行完整类型专用目标预检。
- 计划最短周期为 60 分钟；页面支持创建、暂停、启用、删除和刷新。普通脱敏计划组件过滤私有类型，避免混淆。第二批补齐固定档案集合明细及逐档案历史入口，展示最近开始 / 完成时间；“查看最近执行”复用既有 LastBatchId 只读查询，直接显示成功 / 失败明细并可仅重试失败项。记录过期或权限变化时显示安全错误，不自动再采集。
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
- `--simulator-config-tests`：**15/15 PASS**。覆盖新连接完整回读、回读连接失败后独立恢复、型号版本漂移拒绝和回滚版本漂移拒绝，验证重连过程不遗留同时存活的连接。
- `--config-backup-schedule-tests`：**15/15 PASS**。
- Web 3 个测试文件：**30/30 PASS**（CompleteConfigurationBulk 11、ConfigBackupSchedules 8、SimulatorConfiguration 11）。覆盖预览、精确选择、过期响应、失败重试、计划生命周期、安全错误、部分加载失败、手动刷新并发和 KeepAlive 暂停恢复；新增计划最近执行只读查询 / 错误、固定档案历史入口、JSON 比较下载内容与文件名验证。
- `vue-tsc --noEmit`：PASS。

测试使用 Next Workspace 内最小隔离 fixture；fixture 不代表真实 SSH / Telnet / 恢复互操作。完整 Service harness、最终静态构建、浏览器整体验收和提交由根集成统一执行。

## 仍然阻断的能力与数据边界

第二批只读审查原版 `D:\study\NetConsole-Workspace\NetConsole` 源码，未访问其运行数据：`src/netconsole/services/config_collection_web_service.py`、`config_collection_job_handlers.py`、`backend/api/config_collection_router.py` 及 renderer `api/configCollection.ts` 提供配置采集、内容、历史差异、导出和固定 `save force` 的预览 / 确认。未发现设备完整文件上传、staging 或 native replace 的实际恢复实现。`ground_unattended/task_scheduler.py` 的 `CONFIG_RESTORE` 仅为任务类型枚举，不能代表执行实现；`job_center/handlers/database_jobs.py` 的 `database_backup_restore` 调用的是本地 SQLite 数据库恢复。`docs/storage/BACKUP_AND_RESTORE.md` 说明局点迁移包 / 数据库恢复，均不能当成网络设备整机恢复。

Next 的 `tools/netmiko_worker.py` 仅实现 SW 受控 `configuration commit`，没有已验证的 replace 交互或完整配置传输操作；`ISimulatorConfigurationSession.SupportsNativeCheckpointRestore` 在真实 transport 仍为 false。因而目前缺口同时包括：能够实际生成并枚举唯一新检查点的设备、已验证的 replace 确认 / 完成提示符、任意历史原件的有界传输 / staging 与清理，以及上述路径后的完整重新连接和业务探针真实验收。没有可直接迁移的原版整机恢复代码。

真实 SW 的 `configuration commit` 未产生可读取检查点，既有真实 HTTP 证据为 `reports/r70-configuration-http-20261108-000030.json`、`NATIVE_CHECKPOINT_NOT_CREATED`。真实 session 的 native replace 交互能力仍关闭。没有可验证检查点时不执行描述变更或 replace；本轮没有绕过此阻断。

任意历史完整配置的全量传输 / staging / replace 和真实整机恢复仍待具备可验证能力的授权设备。不通过逐行合并或描述恢复冒充整机恢复；fixture 中的 rollback 验证也不能标记 live 整机恢复通过。

未读取或写入旧 NetConsole 的 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未复制真实数据根。真实读操作只产生 Next 隔离 runtime 的新完整备份及元数据；验收计划已删除，备份作为证据保留。未推送、未合并、未发布；由根集成统一管理 Git 与 DevStatus。
