# 任务中心验收

日期：2026-11-06。状态：PASS（本机隔离调度/API/UI）。整体业务验收仍 PARTIAL。

任务列表与命令明细读取服务端真实 run/Raw/Facts 状态。固定只读多命令计划复用既有 task-definition/run；命令按序执行、独立 Raw，成功终态需要完整 manifest。任务详情、取消、失败重试、重启中断恢复直接按 run ID 查询，不受最近 100 条窗口限制。

- `POST /api/system/tasks/{runId}/cancel`
- `POST /api/system/tasks/{runId}/retry`
- `POST /api/system/tasks/{runId}/resume`
- `GET /api/system/tasks/{runId}/commands`

动作由服务端先检查 SUPERADMIN 和当前 Site scope，再调用 BoundedCollectionScheduler；不接受客户端指定权限范围。父计划保留目标快照、子 run 关联与部分失败聚合。

当前完整 Service harness、并发 5/5、Raw/Facts 13/13、Vue typecheck、Vitest 8 files/23 tests、HTTP 多命令计划与已执行的浏览器场景 PASS。历史 Mutex 是测试协调记录，串行 service/browser 门禁已通过，不再阻断。

当前 AC1/AC2/SW Netmiko 只读登录/命令通过；FIT-AP 不独立连接。业务任务端到端、双 AC HA 与实际硬件恢复仍需独立证据，不能由连接测试替代。
