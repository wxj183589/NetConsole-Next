# 任务中心验收

状态：PASS（隔离调度与离线浏览器）。任务列表读取真实服务端结果，命令明细读取真实 Raw/Facts 状态；新增取消、失败重试和服务重启中断恢复入口。三条操作 API 均在服务端先执行 SUPERADMIN 与当前 Site scope 校验，再调用既有 `BoundedCollectionScheduler`，不接受客户端传入权限范围。

- `POST /api/system/tasks/{runId}/cancel`
- `POST /api/system/tasks/{runId}/retry`
- `POST /api/system/tasks/{runId}/resume`

验证：Service build、Tests build、完整 Service harness、Vitest `5 files / 10 tests`、Vue typecheck、Web contract `4 checks`、1920/2560 浏览器 fixture 均 PASS。真实设备任务仍受模拟器凭据阻断。

状态：PASS（API/UI本地）。任务中心提供固定只读命令计划入口，按顺序创建并执行 H3C 多命令任务；`/api/system/tasks/{runId}/commands` 返回命令级状态、Raw/parse 状态和错误。任务页面可打开命令明细，取消、重试、恢复按任务 ID 直接查询并由既有服务端 Site scope/SUPERADMIN 门禁保护。`web_auth_e2e.py` 已验证多命令计划创建与执行提交；浏览器 service-backed gate 仍受已有 Mutex 影响。
