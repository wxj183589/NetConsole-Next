# Traceroute 与定时配置备份本机验收

日期：2026-11-07。分支：`codex-A/netconsole-next-r3-dual-ac`。功能提交：`2927b35dfb6c9d5ae7e82f83548133f13b1668e6`；进度读取并发修复 `253b8144226091337227f29dca93c9378ebe99af`。仅本地提交，不 Push、Merge、Release。FD-P2-08、FD-P2-09 为 `COMPLETED_LOCAL`，全产品继续 **PARTIAL / REAL_DEVICE_PENDING**。

## Traceroute：FD-P2-08

- 接入既有网络诊断 API、目标选择、进度、取消及终态历史；`TRACEROUTE` 与 PING/TCP/DNS/LLDP 共用每批 128 台、512 项、4 路并发限制。
- 使用 .NET ICMP TTL 探测，不启动 shell 或设备 CLI。最多 30 跳、每跳最多 1000ms，DNS 与探测共享 100–30000ms 总期限；解析一次，IPv4 优先，支持 IPv6。仅实际目标地址的成功响应表示到达目标。
- 区分中间节点、超时、不可达及其他响应；超时、取消和错误保留已取得的部分路径，不把无响应节点解释为拓扑事实。展示探测来源为 Service 主机。
- 准入、DNS 前后及每跳前后复核当前账号仍为启用的 SUPERADMIN，以及原 Site、库存 UUID、Profile、地址、端口和启用状态。撤权或目标变化停止后续探测，返回 `DIAGNOSTIC_SCOPE_REVOKED`。
- JSON 历史每文件最多 4MiB、每项最多 30 跳，仍按当前 Site 过滤。旧历史缺少可选 `traceRoute` 字段保持可读。
- 本批修复三个由新测试暴露的旧诊断缺陷：终态发布早于持久化的竞态、成功与取消混合被误报 SUCCEEDED，以及 PARTIAL 批次历史被子项终态判断误过滤。

## 定时配置备份：FD-P2-09

- 新增 `/api/system/config-backup-schedules` 的 GET/POST、`/{id}` PATCH/DELETE；仅当前有效 SUPERADMIN 可管理自己的计划，执行前重新检查模块、当前所有者、Site 和精确 Profile/库存关联。
- 每计划 1–32 个独立 H3C Profile，最多 64 个计划、4 个同时执行的计划，间隔 60–43200 分钟。FIT-AP 继续经 AC 管理，不建立独立连接。
- 计划复用既有 `ConfigBackupService`、任务中心、`BoundedCollectionScheduler`、Raw 与 CPython/Netmiko。每次重新读取当前端点和普通 Profile 密码；没有第二套连接或凭据体系。
- RUNNING/SAVED/STARTUP_INFO 类别分别保持原只读命令、独立版本、同类 diff、脱敏和 no-store 下载。STARTUP_INFO 是启动文件指向，不代表启动文件正文或恢复功能。
- 创建和恢复启用后先等待完整间隔；15 秒检查一次到期。每计划不重叠，离线错过多个周期最多补一次，从实际准入时间计算下一周期；没有追赶循环。暂停/删除阻止后续准入，已准入的只读任务继续受既有任务生命周期控制。
- `<dataRoot>/backup/config-schedules.json` 为 version 1、有界 1MiB、flush 后原子替换；只存所有者和 Profile 引用等计划元数据，不持久化密码、端点、会话或连接参数。保存失败不报告成功。
- 重启遗留 RUNNING 标记为 INTERRUPTED；已过期时推迟完整间隔，避免不确定重复执行。损坏/不可读计划文件原样保留，计划子系统返回 503 且不准入任务，Service 其他功能继续工作；修复文件并重启后恢复。
- 已移除 Profile 的在途批次仍按真实终态释放槽位。完成批次元数据最多保留 128 条，运行中批次及未持久化终态的定时批次受保护；先保存计划终态再确认释放。该上限不删除 Raw、配置版本文件或采集历史。

## 验证

后端最终构建与回归来自提交前相同源码的工作树；本批没有生成新的离线部署候选。

| 门禁 | 结果与范围 |
| --- | --- |
| Release Service/Tests build | 0 warnings / 0 errors |
| 默认完整 Service harness | session89977 EXIT0；包含策略6组、worker预算6组、重连172、ZTE12、Site13、VLAN6、publication14、诊断证据96、全机诊断helper30及既有回归 |
| 网络诊断 / Traceroute | 4/4、14/14 PASS；真实回环 ICMP、受控 DNS/TTL fixture、共享期限、30跳、IPv6、撤权/目标变化、排队取消、部分结果与重启 |
| 定时备份 / 原备份 | 15/15、9/9 PASS；包含所有者/模块/目标变化、当前端点与密码、无重叠/补跑、持久化故障、删除Profile与元数据上限 |
| 定时计划 HTTP | session44093，15 checks PASS；实际 Service → CPython/Netmiko → 合成回环 SSH，以及实际 HTTP→回环 ICMP；重启、到期一次、无补跑、损坏隔离 |
| 原备份 HTTP | 同 session44093，28 checks PASS；三类别、版本、diff、权限、脱敏/no-store、零设备配置写入与连接释放 |
| 前端 | 2927b35全量16 files / 159 tests、typecheck/build PASS；253b814定向ToolsView12/12、typecheck与最终Vite2087模块构建PASS |
| 定时计划 Chrome | 11 scenarios PASS，1920×1080 / 2560×1440；源码 UI/API mock，3张截图已检查 |
| Traceroute Chrome | 8 scenarios PASS，1920×1080 / 2560×1440，浏览器 DSF 1 / 1.25；源码 UI/API mock，6张截图已检查 |
| 原备份 Chrome | 9 scenarios PASS；为新增计划表限定配置历史表选择器，既有行为回归通过 |

浏览器 API mock 不证明后端持久化；后端重启证据来自独立服务测试和真实 HTTP fixture。浏览器 DSF 1.25 不代表 Windows 原生 125% 缩放验收。未重复网络 worker 专项、终端/Linux/Jump 浏览器或离线十门禁，它们保留所属历史提交证据。

## 证据与数据边界

- Workspace：`D:\study\NetConsole-Next-Workspace`。
- HTTP 报告：`reports/config-backup-schedule-http-20261107.json`；最终 fixture：`test-data/config-schedule-e2e-jf5vo58e`。该隔离 fixture 最后故意保留损坏计划文件用于故障隔离验证，不是产品数据。
- 浏览器：`build/config-backup-schedule-browser/acceptance.json`、`build/network-diagnostics-browser/acceptance.json`；截图与报告一起保留。
- Traceroute 完整 harness fixture：`test-data/network-traceroute-6972931eb37d42e3a6a58517249a5986`。保留既有 SQLite 证据，不批量清理测试数据。
- 只使用 Next Workspace 内隔离数据及合成回环，不访问/复制旧 NetConsole 真实数据根；设备配置写入为 0。本批未重新登录 HCL 设备，既有 AC1/AC2/SW 只读结果不自动证明当前型号、HA或实时AP归属。
- 自有测试 Service/Python/SSH 目标已退出；用户 Vite PID90348、`127.0.0.1:18792` 监听保留。

## 剩余工作

当前离线候选仍为 `build/service-cache-fix-20261107`、ProductVersion `1.0.0+ef5721354fc56004f018a2b851b1aee9fd8b0bdd`，不包含本批 Traceroute/计划功能。目标机安装、SCM、升级卸载、跨机 TLS、Windows 原生125%及真实设备兼容仍待独立验收。

稳定厂商 GroupEvidence、正式 candidate/identity/replacement 权威写合同与可靠终止、Domain/group 生命周期、startup 正文/restore、其他厂商完整结构化业务、双 AC HA/实时归属和剩余工作台继续由动态任务池推进；不从缺席推断 ENDED。

## 最终检查点

`253b814` 修复同代并发进度GET：同一代只保留一个在途请求，重试按钮显示忙碌；取消切换新代后可读取终态，迟到旧RUNNING或错误不会覆盖终态或重启轮询。新增定向测试覆盖读取失败→挂起重试→重复点击→取消→新终态→旧RUNNING迟到。ToolsView12/12、typecheck和最终Vite2087模块构建PASS；没有声称最终全量160项已重跑。浏览器截图早于该修复。文档提交由Git及DevStatus记录。总入口：[运行状态](full-day-autonomous/AUTONOMOUS_RUN_STATE.json)、[任务池](full-day-autonomous/NEXT_TASK_BACKLOG.md)、[测试矩阵](full-day-autonomous/COMPLETE_TEST_MATRIX.md)、[交接](full-day-autonomous/FINAL_HANDOVER.md)。
