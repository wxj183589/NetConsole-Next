# 多任务并发验收

日期：2026-11-06。整体状态：**PARTIAL**；以下PASS仅限列明的本机隔离范围。最终功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；文档以所属提交为准。

## 当前共享预算与批量入口

同一Service实例的采集、单台连接测试、网络WebShell和Jump探测共享16个CPython worker槽；启动前获取、等待可取消，连接45秒/探测30秒期限覆盖等待，实际退出后幂等归还。预算不是SSH连接池，不跨Service实例共享。`NETMIKO_WORKER_BUDGET_TESTS=PASS (6 groups, shared limit 16)`，使用真实本机合成子进程覆盖饱和、取消、启动/退出竞态和释放。

专用批量入口复用既有H3cBatchTaskService和有界调度器，固定1–64个明确库存UUID与只读版本命令；整批可见性/兼容/精确关联预检失败不创建任务。精确映射在所有scope捕获后、持久化任何任务前复核，重绑或映射缺失返回409。定向8/8、完整Vue12 files/82 tests/typecheck、Chrome mock4组及guard后实际HTTP→Netmiko→合成SSH14 checks PASS。

最终Release构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖；`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。旧a3954a5候选保留为历史，当前候选不是正式发布。

## 既有调度/终端并发证据

全局bounded queue、max concurrency、设备级semaphore、失败独立重试、取消、Interrupted显式resume保留；任务详情/取消/重试/恢复按ID查询，不受最近100条窗口限制。`CONCURRENCY_P1_TESTS=5/5 PASS`与H3C Raw/Facts13/13为既有合同证据，已由Terminal reason最终修复后的默认完整Service harness（session `69905`，EXIT 0）再次覆盖；Jump最终HTTP_NETMIKO与RESTART_DELETE两项也PASS。批量连接8组已纳入默认完整harness，配置备份6组保持独立入口。

Jump 回环集成另覆盖三路会话、取消、资源释放与错误隔离；Linux HTTP/WebSocket 和最终 Edge 浏览器覆盖多会话、并发限制、活动/完全静默撤权和 SSH 释放。网络终端完整 E2E 覆盖取消及重连。

上述模拟测试不证明真实设备并发容量、现场稳定性或双 AC HA；这些仍待独立验收。无设备配置写入、旧真实数据访问或远端发布操作。
