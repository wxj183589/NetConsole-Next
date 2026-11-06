# Netmiko 设备连接边界

状态：`IMPLEMENTED_OFFLINE / REAL_DEVICE_PENDING`

网络设备 CLI 的生产自动采集入口现在由 `H3cSshDeviceSessionFactory` 和
`ZteCollectionService` 默认创建 `NetmikoWorkerH3cSshTransportFactory`。它启动
`tools/netmiko_worker.py`，通过逐行 JSON 请求把设备参数传给 CPython，worker 使用
`netmiko.ConnectHandler` 建立连接并执行 `send_command`。密码只在本机进程管道中传递，
不会进入命令行、任务定义、Raw、日志或 HTTP 响应。

## 驱动映射

| 厂商 / 协议 | Netmiko `device_type` |
| --- | --- |
| H3C Comware SSH / Telnet | `hp_comware` / `hp_comware_telnet` |
| Huawei VRP SSH / Telnet | `huawei_vrp` / `huawei_telnet` |
| Cisco IOS、IOS-XE、NX-OS | `cisco_ios`、`cisco_xe`、`cisco_nxos`（各自 SSH/Telnet） |
| ZTE ZXROS SSH / Telnet | `zte_zxros` / `zte_zxros_telnet` |
| Ruijie SSH / Telnet | `ruijie_os` / `ruijie_os_telnet` |
| Juniper Junos SSH / Telnet | `juniper_junos` / `juniper_junos_telnet` |

映射只接受已安装的 Netmiko driver。未知型号返回 verification pending，不能静默回退到
另一个厂商。依赖锁定在 `tools/netmiko_requirements.txt`。

## .NET 与 worker 的职责

- .NET：任务队列、Site scope、凭据读取、取消、超时、进程回收、HostKey Catalog、Raw/Facts 和错误码。
- Netmiko：SSH/Telnet 连接、认证、提示符识别、分页能力、命令发送和回显读取。
- 业务 parser、命令白名单、事实模型和 SQLite 不进入 worker。

worker 连接后先返回 SSH SHA256 指纹；.NET 自动首次信任或轮换（包括旧 STRICT 档案），
不弹出确认窗口。Telnet 不使用 HostKey。现有 `SshNetH3cSshTransport` 仅保留为显式
合成 wire test transport，不是生产默认入口。

## 凭据

H3C profile 的密码存于设备 profile 记录，按 profile ID 读取；共享或重复的
`CredentialReference` 不会导致设备间串用。创建时输入密码，后续测试、采集和终端会话自动
读取；留空更新保留旧值，明确 `clearPassword` 才清除。当前 SQLite 字段是本机受 ACL 保护的
明文字段，尚未宣称数据库级加密。HostKey 与登录密码分开管理。

## 当前边界

- H3C 自动采集、连接测试和网络设备 Web Shell 使用同一 worker；SSH Shell 支持远端 PTY 尺寸调整，Telnet 不支持远端调整。
- 驱动按关联设备的厂商选择；Cisco 需要明确的 IOS、IOS-XE 或 NX-OS 平台，未知厂商或平台被拒绝。没有关联设备的旧 H3C 档案保留 H3C 语义。
- Site Jump Host 测试和设备连接在认证成功后自动记录首次指纹及变化，并检查配置并发变化。Telnet 不套用 SSH Jump Host。
- ZTE 独立档案按档案读取密码，认证后记录并轮换设备指纹；设备管理页面已支持新增、凭据更新和采集入口。本机凭据 API 集成与页面组件测试通过，真实 ZTE 设备仍待验证。
- Linux 服务器 inventory 和交互终端使用独立的 SSH.NET ShellStream 传输，不纳入网络设备 Netmiko worker 或 Site Jump 策略。密码随服务器记录保存，普通视图不返回明文；SSH Host Key 在认证成功后自动首次信任或轮换。loopback SSH 模拟器已覆盖 HTTP/WebSocket、Site 隔离、ANSI/中文、PTY resize、重连、多会话、录制、权限撤销、凭据及服务重启；Linux 专项浏览器 E2E 也已通过。Linux 真实主机仍待验收；Web 默认仍限 loopback，不代表跨机访问已实现。
- 当前 Netmiko 对 HCL AC1/AC2/SW 的只读命令验收已通过，证据为 Workspace `reports/netmiko-simulator-20261106.json`；FIT-AP 仍经 AC 管理。旧 SSH.NET/DPAPI 路径只作历史诊断记录。真实 H3C/AC/AP/ZTE 设备、双 AC HA 与实时 AP 归属仍为 `REAL_DEVICE_PENDING`。

## 本轮定向回归

2026-11-06 当前连接增量：采集、连接测试与网络 Web Shell 共用有界 AUTO 协议策略；显式允许的 H3C 旧 Host Key 协商兼容最多再尝试一次。所有网络 worker 与 Jump probe 共享每 Service 进程 16 个名额，等待可取消，实际子进程退出后才归还。没有新增连接池或任意命令重放，通用断线自动重连仍未实现。策略与原版差异见 [连接策略验收](NETMIKO_CONNECTION_POLICY_ACCEPTANCE_20261106.md)。

设备页现有专用批量连接入口，按精确库存 ID 创建固定只读 `display version` 任务计划，复用已有调度、报告与取消；只支持已关联有效档案的独立 H3C。预检和执行 scope 捕获间的库存重绑会在任务写入前拒绝。详见 [批量连接验收](DEVICE_CONNECTION_BATCH_ACCEPTANCE_20261106.md)。

本批功能提交 `8320e2f0653c0f295700883c1900eef0a0690632`；Release 构建 0 warnings / 0 errors；终端原因码修复后的最终默认 Service harness EXIT 0（session69905）；连接策略 6 组、共享预算 6 组、批量连接 8/8、Netmiko worker 24/24、Vue 12 files / 82 tests 与 typecheck、Chrome 批量页面 mock 4 组及真实 Service→Netmiko→合成 SSH 14/14 均 PASS。Jump 最终回环/重启删除两项 PASS，首次 FAILED 原因码有确定性回归。当前准确 SHA 离线候选五项运行门禁 PASS，见 [本批验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)；下列旧计数保留为历史。

以下为前序 Linux/Jump 批次的历史测试记录，计数保留当时范围：

- Linux server catalog/concurrency/migration：`dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release --no-restore -- --linux-server-tests`，PASS。并发相同 Host Key 现按同一配置版本幂等成功，历史只记录一次。
- Linux HTTP/WebSocket + Paramiko loopback SSH simulator：`py -3.13 tests/linux_ssh_e2e.py`，PASS；没有连接真实 Linux 主机。
- ZTE 凭据 HTTP 回归：`py -3.13 tests/zte_credential_e2e.py`，PASS。
- Web：`npm run typecheck` PASS；`npm test` 8 files / 23 tests PASS。
- Netmiko worker：`py -3.13 tests/netmiko_worker_test.py`，20 tests PASS。
- Linux 浏览器 E2E：`py -3.13 tests/linux_ssh_e2e.py --browser`，PASS（本机通过 `NETCONSOLE_E2E_BROWSER` 使用已安装 Edge）；测试自动在 `test-data` 隔离临时目录构建当前 Vue Webroot，并在结束时清理。覆盖服务器 CRUD、连接测试、键盘控制键/中文/ANSI、PTY resize、多标签/分屏、两类重连及 1920×1080/2K 视口；HTTP 门禁另验证静默撤权后关闭 WebSocket 并释放 SSH。
- Jump Host：`py -3.13 tests/ssh_jump_host_e2e.py` PASS，真实回环 direct-tcpip 转发覆盖两 Site、采集/连接测试/Web Shell、并发/取消、错误分类、指纹与重启；`node tests/ssh_jump_host_browser_e2e.mjs` 的 Chrome mock API 界面门禁 PASS。
- 完整 Service harness PASS；离线工具 4 + 3 tests PASS。最终组合候选运行证据见 [本批验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)。
- 浏览器验收连接的是 loopback Paramiko SSH 模拟器，不是 Linux 实机；真实 Linux 主机和跨机 Web 访问仍待验证。

## 离线部署

部署包必须同时包含 CPython 运行时、`netmiko==4.7.0` 及其锁定依赖。服务优先使用显式环境变量，其次查找服务目录下 `tools/netmiko/` 中的离线包，最后查找 PATH 上的 Python。若 worker 不存在、依赖缺失或 driver
未安装，连接失败并保留安全错误码，不回退到另一套 SSH 客户端。

Windows x64 离线包的构建、校验与环境变量配置见 [离线部署](NETMIKO_OFFLINE_DEPLOYMENT.md)。
Service 离线候选已通过组合构建、隔离及搬迁测试，目标机需要 .NET 10 ASP.NET Core Runtime；正式安装与跨机验收仍待完成。
