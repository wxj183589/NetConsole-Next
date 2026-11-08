# R7.7 配置变化、备份与恢复

状态：**PARTIAL**。本轮新增有效版本变化检测和差异UI，未执行真实设备配置写入、整机恢复或失败回滚。

变化检测复用完整备份manifest与文件完整性检查；只比较同profile/device/site、有效完整性和有序版本/时间的最近两条。无baseline、损坏/不匹配、重复版本或不可读取返回明确UNKNOWN，不将脱敏相同误称原始相同。UI显示变化与原因并通过已有差异入口查看精确版本对。产品提交dd72333；API未知/禁用边界1cd8996。

验证：最终候选HTTP14/14、实际Chrome1920/2560浏览器16/16；不连接设备，不产生采集任务。证据 r77-drift-http-20261108-233146-628518/result.json 和 r77-drift-browser-2026-11-08T15-31-49-266Z/result.json。
Service全量EXIT0：Simulator26/26、Backup12/12、Schedule19/19、Checkpoint18/18、Readiness16/16；r77-api-service-full.log。
最终候选离线两模式配置子门禁通过，显式DLL完整门禁通过；APPHOST整体因Traceroute权限失败。回环SSH合成目标不是真实恢复。

共享实例的配置批次历史/存储浏览器通过；本轮双AC仅display只读命令。历史受控描述Writer见R7.6报告，不能计作本轮恢复。历史SFTP_SUBSYSTEM_UNAVAILABLE、设备文件上传/hash、native replace交互、恢复/回滚后业务读回仍阻断；本轮未重复失败认证或绕过准入。自动变化告警尚未接入故障事件，不能仅凭变化检测宣称已实现。
