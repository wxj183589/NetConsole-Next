# R7.3 配置管理与恢复

2026-11-08，codex-A。基线 `220c979`。专项结论 **PARTIAL**，两批本地产品功能和定向回归完成，实际整机恢复仍未通过。

## 容量与保留策略

新增 `GET /api/system/simulator-config/storage?profileId=...`。复用完整配置服务的目标授权和路径校验，以设备当前关联、SUPERADMIN及Site权限读取容量。查询不连接设备，不创建不存在的配置目录。

API返回设备清单数量、占用字节、设备数量上限、共享配置额度、磁盘余量和测量时间。受损清单及孤立文件计入占用，不用有效版本列表替代实际磁盘统计。全局权限可读取共享存储占用，限定Site权限只读取本设备占用，共享用量保持null。

配置页新增按需容量检查入口，展示满额停止新增采集、保留所有原件和历史的现有策略。明确区分展示上限、128条终态批次元数据保留及设备配置版本的长期保留。页面不会自动删除配置文件。

## 手动批次历史与失败设备重试

此前终态批次已持久化，手动批次离开页面后缺少发现入口。新增 `GET /api/system/complete-configuration/batches`，复用现有批次历史存储和 `GetBatch` 的当前owner、role和Site检查。合并运行中批次与持久终态批次、去重，按创建时间和ID排序，最多返回50个可读完整配置批次。

配置页可刷新批次历史、选择旧批次并查看逐设备结果。失败项沿用已有“仅重试失败设备”动作，当前权限及目标在原备份入队路径重新核验。成功设备不会随失败设备一起重采。查询和查看结果均不启动设备会话。

## 定向回归

| 测试 | 数量和结果 | 证据等级 |
| --- | --- | --- |
| SimulatorConfiguration Service harness | 21/21 PASS | 隔离fixture，包含原有Writer中断/回滚回读及新增容量统计/权限/不连接不删除、损坏目录和dangling链接 |
| CompleteConfigurationBatch Service harness | 20/20 PASS | 隔离fixture，包含新增手动批次跨服务对象重建发现、混合Site整批隐藏、claims撤销及role拒绝 |
| Web容量组件 | 4/4 PASS | mocked API，按需读取、共享用量未知、迟到响应和错误profile |
| Web模拟器配置组件 | 12/12 PASS | mocked API，原行为保持 |
| Web完整批次组件 | 16/16 PASS | mocked API，新增历史选择与仅失败设备重试 |
| Web typecheck | PASS | `vue-tsc --noEmit` |
| Release Service/Tests build | PASS，0警告0错误 | 独立 `build/r73-config` ArtifactsPath |

独立命令使用 `--simulator-config-tests` 和 `--complete-config-batch-tests`，未改共享测试入口。首次构建新增测试误用了本文件不存在的Expect helper，改用既有异步Reject helper后构建和上述组通过。HTTP、静态Web浏览器和最终完整回归由主集成补证。

## 实际恢复边界

沿用现有原件、哈希、独立回读预检、模型版本匹配和Writer恢复状态机。当前未实现经设备验证的完整历史文件上传、staging、设备原生替换、独立整机回读或故意失败整机回滚，`WRITE_PASS`、`READBACK_PASS`、`CONFIG_HASH_PASS`、`BUSINESS_PROBE_PASS`、`ROLLBACK_PASS` 均未提高。

R7.2已有SFTP不可用、SW无可枚举检查点的历史记录。本专项未主动连接设备，当前模拟器连通性与恢复后的只读回归以主集成设备报告为准。没有把fixture、存储原件可用或预检通过记为实际恢复成功。

终态批次历史已有跨重启持久性，正在执行的手动批次尚未持久化逐设备执行进度。Service中断后不自动重放结果未知的操作，完整中断恢复仍待后续产品闭环。

数据仅为Next Workspace最小fixture及只读容量检查，未访问旧真实数据根，未改写设备配置，19个既有静态资源保持原样。提交由主集成统一完成。

## 对抗性复核

发现配置根或profile目录被普通文件占据时，初版容量统计误报0占用。现将这种损坏目录明确返回存储不可用。复用的路径检查同时补齐dangling符号链接识别，防止缺失链接目标被当成不存在的安全目录。回归确认先检查权限再读取损坏根，原始阻塞文件保留，dangling链接目标不创建，无设备连接。

批次历史增加混合Site回归。一个同时含本Site和其他Site的批次，权限收缩后整个批次隐藏，ID、设备数和失败项不以部分投影暴露。终态metadata重载后保持该语义，claims全撤销返回权限拒绝。损坏批次存储的列表查询返回既有503，损坏原件不重写。旧版本metadata格式和字段保持兼容。
