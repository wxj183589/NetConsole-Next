# R4 任务调度验收报告

当前 HEAD：`2a4d65f`。

本地结果：完整 Service harness EXIT 0；`CONCURRENCY_P1_TESTS=6/6 PASS`；`CONFIG_BACKUP_SCHEDULE_TESTS=15/15 PASS`；配置恢复 `11/11 PASS`；新增 `GET /api/system/tasks/{runId}` 使用 URL 编码、Site scope guard，并对不存在或越权任务返回 404。前端 Vitest 17 files/200 tests、typecheck、Vite build PASS。

边界：本地 fixture 和回环设备不代表现场并发容量；重启后真实设备恢复、权限撤销中的现场任务以及跨机部署仍待单独验收。
