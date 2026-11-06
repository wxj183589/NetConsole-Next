# Netmiko 断线重连专项验收

日期：2026-11-06

状态：`PARTIAL / REAL_DEVICE_PENDING`

本文记录 NetConsole-Next 对原版 NetConsole 断线处理语义的继承边界，以及本批新增的 Netmiko Worker 重连策略。本文只描述只读 CLI 采集和连接恢复，不授权或验证任何设备配置变更。

## 原版参考语义

原版 `src/netconsole/services/netmiko_connection.py` 的 `run_netmiko_with_retry` 会遍历 `ConnectionManager` 生成的候选连接目标；每个目标只建立一次连接并执行完整 callback，最后释放连接。它不是同一端点的定次数重连器。由于 callback 可能包含写命令，Next 不直接重放这个 callback。

原版的持续运行服务提供了可复用的故障边界：

- `mesh_link_resident_polling_service.py` 在连接或传输失败时关闭旧连接、执行可中断退避并建立新连接；初始化命令在新会话执行，解析失败不触发重连。
- `online_mr_collector.py` 的本次失败采样返回失败标记，重连只服务后续采集，不把旧命令结果重放为成功。
- `ssh_resilient_worker.py` 只对明确的连接 reset 类异常恢复，并有明确的重连上限。

## Next 策略

`src/NetConsole.Next.Service/NetmikoRetryTransport.cs` 是现有 Netmiko Worker 外围的轻量恢复层，不是新的 SSH/Telnet 客户端、连接池或认证框架。普通网络设备连接仍由 `tools/netmiko_worker.py` 的 CPython + Netmiko `ConnectHandler` 完成。

- 一个逻辑会话最多使用两次额外重连，退避分别为 250 ms 和 1000 ms。
- `NetmikoReconnectBudget` 可由同一任务的 SSH/AUTO 备用传输共享，备用协议不会重新获得预算；底层 Worker 仍受全局 16 个进程的 `NetmikoWorkerBudget` 限制。
- 创建新 Worker 前释放旧 transport；Worker 进程只有确认退出后才归还全局槽位。
- 退避和建连沿用调用方的取消令牌和任务总 timeout，不为每次断线重新开始任务时限；`Abort`、`Dispose`、取消和服务停止都会中断退避或建连。
- 只有明确的连接丢失、连接建立超时，以及 Jump Host 不可达/转发通道网络失败可进入恢复路径。认证失败、Host Key 不匹配或缺失、驱动/协议错误、权限拒绝、未知 Worker 错误和输出上限错误不重试。

## 命令重放边界

命令恢复只允许当前厂商和当前已验证驱动的精确只读命令白名单。已成功完成的命令不会重放，恢复只针对当前尚未获得成功响应的命令。

以下情况直接结束当前命令并保留失败状态：Netmiko `ReadTimeout`、命令超时、部分回显、命令执行中网络中断、Worker 在请求发出后 EOF/管道断开、交互 Shell 读写或 resize。命令执行阶段的 Worker 管道 EOF 映射为 `H3cCommandInterruptedException`，避免把结果未知的命令再次发送。交互终端不套用自动命令重放策略。

## Host Key、Jump Host 与权限边界

每次物理重连都重新执行原始 Host Key verifier。H3C 在成功认证后提交本次 staged Host Key；ZTE 使用自身 profile 的 compare-and-swap 提交逻辑。新会话提交失败会释放新 Worker，且不会发送下一条命令；失败的 pending key 会被标记为失败，不覆盖上一次有效指纹。

Jump Host 的业务配置在逻辑会话中冻结。重连前会重新读取并比较地址、端口、用户名、凭据引用、启用状态等业务字段；配置变化会停止会话。Jump Host 指纹只在成功认证后按 expected snapshot 条件更新。Telnet 不使用 SSH Jump Host。

任务 scheduler 在连接前、每条命令前和每次重连前重新检查 actor、站点、设备归属、启用状态和端点。撤权会阻止创建下一 Worker，并将已获得的只读证据保留为部分结果。ZTE 独立采集入口也在 admission、出队、连接、每条命令、重连和发布前执行当前 actor/site/profile 检查。

## 离线验证入口

本批新增的定向用例位于 `tests/NetConsole.Next.Service.Tests/NetmikoRetryTransportTests.cs`，覆盖：

- 可重试异常与认证、Host Key、权限、超时、部分输出、未知错误的非重试分类；
- 同一任务跨初始建连和命令恢复共享两次预算；
- 250/1000 ms 退避、取消、Abort、Dispose 和 Worker 退出后的槽位释放；
- H3C/ZTE Host Key 首次信任与两次旋转后的提交/CAS；
- 当前精确只读命令重放、已完成命令不重放、Worker EOF/无效 JSON 不产生空成功；
- 重连前授权 hook 和 Host Key 提交失败后的连接释放；
- 真实 factory 创建的 Worker 子进程中，Telnet 忽略无关 Jump 配置变化，SSH 重连读取最新 Jump 指纹并执行 CAS。

`tests/netmiko_reconnect_e2e.py` 提供 HTTP → Service → Netmiko → 合成 SSH 的隔离回环验证，覆盖一次断线恢复、预算耗尽、任务总 timeout、认证失败不重试、Raw/ledger 完整性和 Host Key 轮换。该脚本要求先构建 Release Service/Tests；本记录不把尚未运行的脚本标为 PASS。

已知的原版测试参考包括 `tests/test_ac_mesh_link_resident_polling.py` 的连接失败重连、解析失败不重连和可中断退避，`tests/test_online_mr_collection.py` 的重连初始化与失败采样语义，以及 `tests/test_fping_v5.py` 的连接 reset 限次恢复。

## 本批定向结果

以下结果由开发执行方与主任务在 2026-11-06 的当前修改集上执行；所有 SSH 故障注入均使用隔离回环合成服务，不连接现场设备。

| 验证 | 结果 | 证据边界 |
| --- | --- | --- |
| Release build | PASS | 最终 Service / Tests 构建 0 警告、0 错误。 |
| `--netmiko-retry-tests` | 163/163 PASS | 包括重连分类、共享预算、Host Key 两次旋转、真实 Worker 子进程异常、Jump CAS、Telnet 隔离与槽位释放。 |
| Python Worker 定向测试 | 29/29 PASS | 验证结构化错误映射、Paramiko NoValidConnectionsError、发送前连接丢失和发送后结果未知的不同处理。 |
| `--zte-tests` | PASS | 既有 profile/parser/current/history 用例通过，新增 `ZTE_OPERATION_BOUNDARY_TESTS=12/12 PASS`。 |
| `--task-site-scope-tests` | 13/13 PASS | 包括命令间撤权、重连前撤权与已完成命令的 partial ledger。 |
| 默认完整 Service harness | PASS / EXIT 0 | session 46633，包含重连163、预算6组、ZTE12、scope13、诊断89及既有迁移/无线/Raw/并发/恢复回归。 |
| HTTP → Service → CPython / Netmiko → 合成 SSH | 13 checks PASS | session 67184；完成回显后断线恢复、两次共享预算、顺序与不重放、Raw完整性、Host Key轮换、命令执行中断线、总timeout、认证失败及凭据不进入任务快照。 |

ZTE 新增 12 个场景包括 admission 拒绝、连接后 actor 禁用、命令间角色降级、启用状态或 host/port/username/Site 变化、发布前撤权、排队期间禁用、重连前撤权，以及仅 Host Key/凭据 revision 更新时允许继续。撤权保留旧 Current，将本次已完成结果保存为 PARTIAL history。

合成 SSH 关闭通道或连接后，Netmiko 可能返回命令中断，也可能等待提示符至读取/任务超时。HTTP 用例验证两种失败终态均不重发命令、无成功 ledger，并确认连接释放；精确异常分类另由 Worker 和 C# 定向检查覆盖。首次完整 harness 暴露了预算测试仍捕获旧 IOException，已改为新的连接丢失异常并在最终完整 harness 复验通过。

最新 Worker 已使用本地 wheel 重建为 `build/netmiko-reconnect-final-20261106`，隔离驱动 / Telnet / JSON 协议 smoke PASS。准确功能 SHA 的组合离线候选由后续文档检查点记录，不以旧候选替代。

## 未完成事项

- 本批未连接现场交换机、路由器、AC、AP、防火墙或工业交换机；H3C、ZTE 及其他厂商的真实设备驱动、提示符、分页、编码和 Host Key 轮换仍为 `REAL_DEVICE_PENDING`。
- 合成 SSH、离线 harness 和 HTTP E2E 不能替代 Windows 离线安装、真实 Jump Host 网络、设备型号兼容性和现场撤权时序验收。
- 本文不宣称其他 Netmiko 映射厂商已经具备对应的业务采集、解析、配置备份或型号兼容能力。
