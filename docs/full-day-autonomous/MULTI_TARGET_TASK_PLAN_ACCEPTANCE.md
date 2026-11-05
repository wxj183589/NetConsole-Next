# 多目标 H3C 任务计划验收

日期：2026-11-05  
分支：`codex-A/netconsole-next-r3-dual-ac`

## 本批次完成

- 新增 `task_plans` / `task_plan_items` 增量 schema v20。
- 一个计划保存不可变的命令计划、创建者 claims revision、目标快照和每台设备的子任务定义/运行 ID。
- 每个目标仍由现有 `BoundedCollectionScheduler` 独立调度，保持全局并发上限和同设备互斥；队列拒绝、权限撤销和失败只影响对应子任务。
- 计划读取和取消前重新执行每个子任务定义的当前 Site claims 校验。
- 父状态聚合为 `QUEUED`、`RUNNING`、`SUCCEEDED`、`PARTIAL_FAILED`、`FAILED` 或 `CANCELLED`，不把部分失败伪装成成功。
- 新增 API：
  - `POST /api/system/h3c/task-plans`
  - `GET /api/system/h3c/task-plans/{id}`
  - `POST /api/system/h3c/task-plans/{id}/cancel`

## 验收证据

- `TASK_PLAN_PERSISTENCE_TESTS=PASS`：隔离 SQLite 验证目标快照、子运行关联和部分失败聚合。
- `CONCURRENCY_P1_TESTS=5/5 PASS`：既有调度器并发、互斥、重试、恢复和历史查询回归通过。
- Service 与测试项目 `dotnet build --no-restore`：0 warnings / 0 errors。
- `git diff --check`：通过。

## 边界

- 本批次没有连接模拟器、修改设备配置或读取密码。AC1/AC2/SW/FIT-AP 的自动登录仍只接受本机 DPAPI Credential Reference；当前凭据引用未保存，不能声称实时认证或 HA 验收通过。
- AP identity replacement/candidate 审核仍是只读投影缺口；本批次没有新增无线身份写入或未经批准的 schema。
