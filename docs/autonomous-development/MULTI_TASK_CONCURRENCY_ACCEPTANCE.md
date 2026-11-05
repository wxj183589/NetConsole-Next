# 多设备并行与任务恢复增量验收

基线：`2343e6b`。本批只修改调度器和隔离测试，不修改schema、真实数据、设备连接或无线/UI代码。

`BoundedCollectionScheduler` 保留全局并发上限和有限队列，并新增按目标设备的异步互斥。相同 target 的连接、命令、Raw 提交和结果发布共用一个设备门；不同 target 仍可在 `MaxConcurrency` 范围内并行。取消、超时、撤权和服务停止继续沿用原边界，等待设备门时也响应任务取消。

失败、超时、取消和重启后 `INTERRUPTED` 的任务可以由任务中心调用显式重试/恢复入口。入口读取原 run 的 definition，重新走当前 definition 校验、当前 actor/Site scope 和队列 admission，创建新的 run；原 run、原始输出和状态保留不变。成功 run 不可重试。重试和恢复只写安全审计引用，不写命令输出、密码或 token。重启不会自动重连旧任务。

验证：`CONCURRENCY_P1_TESTS=5/5 PASS`，覆盖同设备互斥与跨设备并行、失败任务新 run 重试、`INTERRUPTED` 显式恢复、成功任务拒绝重试，以及超过最近 100 条窗口的任务按 ID 查询。任务恢复按 ID 直接读取 Catalog，避免旧任务因 Recent 窗口无法 retry/resume。完整 Service harness 继续作为集成 Gate。单任务多命令仍受现有单命令 Raw manifest 合同约束，不能通过把多条输出塞进一个 Raw 记录来宣称完成；后续需要独立的命令清单、子命令状态和每命令 Raw 关联合同。
