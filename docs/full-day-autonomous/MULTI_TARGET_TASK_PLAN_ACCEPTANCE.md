# 多目标 H3C 任务计划验收

## 2026-11-06 当前状态补充

本批功能已本地提交 `a3954a5`，文档以所属 commit 为准。父计划/子任务/claims 与并发合同继续有效，最后 scope 修复后的完整 Service harness PASS。设备连接使用 Netmiko，普通密码按 Profile 自动读取，不依赖旧 DPAPI 凭据中心；AC1/AC2/SW HCL 只读命令 PASS，FIT-AP 只经 AC 管理。

这不等于多目标业务端到端、正式 AP 生命周期或双 AC HA 已验收，当前仍 PARTIAL。无设备配置写入。

## 以下为 2026-11-05 原批次历史记录

原“凭据引用未保存”结论与只接受 DPAPI 的描述不再是当前架构。

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
