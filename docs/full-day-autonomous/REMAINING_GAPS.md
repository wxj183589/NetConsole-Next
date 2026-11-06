# 剩余缺口与验收边界

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。当前已完成检查点 `9fc499f8d2de08973896a1ea35b35d4e3ff23d16`；文档以所属提交为准。整体状态 **PARTIAL**。

## 当前已闭环

- VLAN `170bf81`：parser 6/6、Raw publication 14/14、HTTP→Netmiko→合成 SSH 25 checks、Chrome 源码 UI/API mock 6/6；诊断 `34b524d`：后端 89/89、HTTP→Netmiko→合成 SSH 21 checks、TasksView 20/20、Chrome 源码 UI/API mock 8/8，均 PASS。
- 最终完整 Service harness session `31025` EXIT 0 PASS（含重连172/172及分类 / Jump CAS 修正）；前端 `pnpm test` 14 files / 124 tests、`pnpm typecheck`、`pnpm build` 均 PASS。
- 当前离线候选 `D:\study\NetConsole-Next-Workspace\build\service-9fc499f-20261106`，ProductVersion `1.0.0+9fc499f8d2de08973896a1ea35b35d4e3ff23d16`，复用已验证 bundle `build/netmiko-reconnect-final-20261106`（CPython 3.13 / Netmiko 4.7 / Paramiko 4）。构建 session `2553` EXIT 0，当前 Vue 与复制前后 driver / Telnet smoke PASS；Production、独立 cwd/隔离数据、PATH 仅 System32、无 Python/worker 覆盖的 E2E session `16200` EXIT 0。`OFFLINE_BUNDLED_RUNTIME`、`OFFLINE_STATIC_UI`、`OFFLINE_NETMIKO_CONNECTION`、`OFFLINE_TASK_DIAGNOSTICS`、`OFFLINE_NETMIKO_RECONNECT`、`OFFLINE_LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 七项均 PASS；重连不重放已完成命令，诊断 TXT/ZIP hash 验证且不重新采集。最终自有 Next Service / Python 进程残留 0、offline-candidate 临时目录 0，用户 Vite PID `90348` / `127.0.0.1:18792` 保留。目标机仍需 .NET 10 ASP.NET Core Runtime，安装/SCM/升级/卸载尚未验收；34b524d 及更早候选仅作历史。
- FD-P1-34 / FD-P2-05 为 `COMPLETED_LOCAL`。本批详细边界见 `../H3C_VLAN_ACCEPTANCE_20261106.md`、`../TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md`、`../NETMIKO_RECONNECT_ACCEPTANCE_20261106.md`；完整门禁见 `COMPLETE_TEST_MATRIX.md`。

- 网络设备采集、连接测试与Web Shell统一CPython + Netmiko；显式SSH/Telnet driver mapping、按Profile自动取密码、认证后自动信任/更新Host Key、取消和连接释放已有本机验证。AUTO/legacy共用策略6组历史证据保留；当前worker29/29、有界恢复C#172/172及HTTP13 checks PASS；AUTO SSH/Telnet路径共享重连预算；legacy协商兼容有独立一次上限，不引入任意命令重放或第二套认证。
- 专用批量连接入口已补齐：设备页明确选择1–64库存UUID，API整批可见性优先及精确Profile关联预检，复用现有计划/调度器/Netmiko，只读display version。8320e2f批次定向8/8、Vue12 files/82 tests/typecheck、Chrome mock4组、guard后实际HTTP→Netmiko→合成回环SSH14 checks PASS；仅SUPERADMIN、启用且独立管理的H3C，FIT-AP拒绝并继续经AC管理。
- 同一Service实例内采集、单台测试、网络WebShell和Jump探测共享16个CPython进程槽；等待可取消、既有连接/探测超时覆盖等待，实际退出后归还。真实本机合成子进程预算6组PASS；这是资源预算，不是SSH连接池，不跨Service实例共享。
- 设备/Profile同事务保存；ZTE凭据API/UI、Linux服务器/交互终端、Site Jump Host回环转发和Settings页面已有本机集成。AC1/AC2/SW Netmiko登录及只读命令PASS；FIT-AP经AC管理；旧DPAPI/SW Exec/独立FIT-AP登录仅作历史。
- AP Group来源身份 `fda2a68`、候选缓存收敛 `2696390`、F10 conservative replacement隔离 `764d764` 已本机验收；Group合同/Projection、Runtime13/13、缓存4/4、F10 3/3 PASS。缓存淘汰不表示组ENDED；F10不构建正式replacement link。
- 配置备份 `e12d062`：设备UUID明确关联Profile，独立H3C目标预检、授权与迟到响应隔离、完整行序/重复次数的有界diff；备份批次定向6/6、当时Web11 files/65 tests/typecheck、Chrome mock6场景、实际HTTP→Netmiko→合成回环SSH19 checks PASS。该批完整Vue结果12 files/82 tests PASS；当前结果见上方9fc499f检查点。
- 8320e2f 批次历史证据（不代表当前候选/测试数量）：最终Release Service/Tests构建0 warnings / 0 errors；Terminal reason 最终修复后的默认完整 Service harness（session `69905`）EXIT 0 PASS；`SITE_JUMP_HTTP_NETMIKO=PASS`、`SITE_JUMP_RESTART_DELETE=PASS`，首次 FAILED 即提供稳定错误码。最终完整harness包含策略/预算、批量8组及既有无线/cache/F10/runtime/Raw/并发回归；配置备份6组仍为独立入口。网络终端、Linux HTTP/WebSocket + Edge（含静默撤权/SSH释放）、Jump Chrome保持各自原证据范围。
- 8320e2f 批次历史证据（不代表当前候选/测试数量）：旧a3954a5离线候选仅作历史。当前候选 `D:\study\NetConsole-Next-Workspace\build\service-8320e2f-20261106`，ProductVersion `1.0.0+8320e2f0653c0f295700883c1900eef0a0690632`；fresh Vite + 内置 CPython 3.13 / Netmiko 4.7 / Paramiko 4；Production、独立 cwd/隔离数据、PATH 仅 System32，无 Python/worker 覆盖。`OFFLINE_BUNDLED_RUNTIME`、`STATIC_UI`、`NETMIKO_CONNECTION`、`LINUX_WEBSOCKET`、`OFFLINE_CANDIDATE` 五项 PASS；STATIC_UI覆盖Linux/Jump/Terminal/Devices批量与备份/ConfigBackups。候选测试启动的 Next Service/Python 残留 0，offline-candidate 临时目录 0；用户预览 PID `90348` 保留。当前候选通过本机隔离验收，候选不是正式发布。

## 仍需实现或验收

1. F10已验证新旧identity、runtime、离线command output与fact: provenance隔离；尚待定义candidate确认/拒绝、identity合并/拆分、replacement权威写入/撤销和revision并发合同，以及可靠关系终止证据与Domain/group生命周期。未验证实时Raw分段完整性，不从缺席推断ENDED。
2. 双AC HA、切换/恢复及实时AP归属；命令返回不能证明主备关系。剩余完整模块工作台及实时多AC业务端到端仍待，fixture/回环不能自动升级为现场业务验收。
3. Windows原生125%显示缩放；现有1920×1080/2K视口和浏览器缩放不能替代。
4. 跨机器Web/TLS；当前服务限loopback，远程范围复核见下节。
5. 目标机安装、SCM、升级/卸载和其他Windows环境；候选仍依赖目标机.NET 10 ASP.NET Core Runtime。
6. 真实H3C、ZTE、其他型号、Linux主机和实际堡垒机互操作：`REAL_DEVICE_PENDING`。
7. 原版完整AC命令/独立稳定组证据、保存/启动配置流程及其他厂商完整结构化业务仍有缺口；driver映射存在不能当作全业务兼容。有界发送前恢复已本机验收；未对发送后未知结果、交互 Shell 或任意命令自动重放，范围见 FD-P1-35。 一次性 `display diagnostic-information` 全机诊断命令未迁移；任务封存 TXT/ZIP 下载不等于原版全机诊断。

## 2026-11-06 远程 Web 范围复核

已只读核对原始全天候开发、Web 控制台重构与 Linux SSH / Jump Host 补充任务，以及 `WEB_AUTH_AND_LAN.md`、`WEB_UI_ARCHITECTURE.md` 和当前认证实现。现有请求覆盖 Web 控制台开发、本机浏览器验收及自动设备登录；未发现明确要求开放 LAN 监听、部署跨机器 HTTPS 或调整防火墙。本批保持既有 loopback 行为，不扩展远程 Web。

`RuntimeSettings` 继续拒绝非 loopback 地址和 `NETCONSOLE_NEXT_WEB_LAN=1`。当前服务为本机 HTTP；会话 Cookie 的 `Secure=false`、固定本机 WebSocket Origin 和本机 bearer 的 `SUPERADMIN` 兼容路径不能直接用于远程入口。默认 `admin/admin`、`superadmin/superadmin` 账号也不能据此作为远程部署认证验收证据。

后续远程 Web 的最小实现需同时完成：

- 显式远程 opt-in、HTTPS 证书和允许的 Host / Origin 配置；配置缺失时拒绝启动远程入口，默认本机入口继续可用，不回退远程明文 HTTP。
- 远程入口只使用 Web 用户会话及当前角色 / Site 授权；Local Manager bearer 仅在本机管理端点有效，不得在远程入口构造 `SUPERADMIN`。已有 Local Manager 本机 URL 契约需同步验证。
- TLS 会话使用 Secure Cookie；同源写操作继续验证 CSRF。登录 Origin、WSS Origin、一次性 ticket、会话撤销和静默终端撤权一并验证，不接受任意 Host 拼出的 Origin。
- 完成默认账号密码的远程启用边界，并复用现有审计存储记录登录、退出、密码修改和重要业务操作；密码、Cookie、bearer 和 ticket 不写入审计内容。当前认证接口尚未形成这些认证事件的审计闭环。

验证门槛：先补绑定 / 证书 / Host / Origin 配置定向测试，再在隔离数据上验证 HTTPS 登录、Secure Cookie、CSRF、远程 bearer 拒绝、角色 / Site 隔离与审计、WSS ticket 过期 / 重放及撤权关闭，并回归本机 HTTP / Local Manager。隔离 TLS 回环通过后仍需单独完成跨机器证书信任和实际网络访问验收；本次只读复核未启动服务、变更监听或防火墙，也未执行这些远程门禁。

## 数据与历史

普通密码为受本机 ACL 保护的 SQLite 明文字段，不使用普通设备 DPAPI 中心。密码不在普通 DTO、URL、命令行、日志、Raw 或报告中回显。未写设备配置，未触碰旧 NetConsole 真实数据根。

历史 Mutex 争用已通过串行测试协调解决；当前完整 Service harness 与浏览器门禁通过，不再作为产品阻断。旧测试失败仍保留于历史日志。

FD-P1-35：`4ee1cb7` 实现 Netmiko 有界发送前恢复；`9fc499f` 最终检查增至 C# 172/172、Python worker 29/29、HTTP 断线注入 13 checks、ZTE 操作边界 12/12、Task Site scope 13/13 PASS。同一逻辑任务最多 2 次额外重连（250ms / 1000ms），共享原期限、取消与 Service 16 个 worker 槽；仅对驱动精确只读 allowlist 中、发送前已确认连接丢失的当前命令恢复。发送后断线、部分回显、ReadTimeout、worker EOF/pipe 和交互 Shell 操作不自动重放。重连前复核当前 actor / Site / target / endpoint；每次物理连接认证后才更新 Host Key。ZTE 撤权将已完成事实封为 PARTIAL 历史并保留旧 Current。

FD-P1-36：`9fc499f` 已退役旧 SSH.NET 网络设备 transport / decoder / 测试，并修复连接异常与 Jump CAS 并发分类；Linux SSH.NET ShellStream 保留。最终 Release Service / Tests 构建 0 warnings / 0 errors；完整 Service harness session `31025` EXIT 0 PASS，重连检查增至 172/172，ZTE12、scope13、VLAN6/publication14、诊断89均 PASS。CI 已转 CPython 3.13 真实 Netmiko 门禁，仅更新 workflow 并验证本机等价门禁，未执行远端 CI。

最终 Jump Host E2E session `65279` EXIT 0：`SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` 两项 PASS，包含连接测试与终端转发拒绝各只尝试一次的新断言。最终进程核验 Next Service / Python 残留 0、`test-data/offline-candidate-*` 目录 0；用户 Vite PID `90348` 与 `127.0.0.1:18792` 监听保留。结构化证据：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-9fc499f-20261106.json`。

统一架构与全入口验收见 [Netmiko统一验收](../NETMIKO_UNIFIED_ARCHITECTURE_ACCEPTANCE_20261106.md)。
