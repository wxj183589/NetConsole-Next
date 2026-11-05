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
- ZTE 独立档案按档案读取密码，认证后记录并轮换设备指纹；ZTE 界面仍待完善。Linux 服务器 inventory 和跨机 WebSocket 仍未实现。
- 真实 H3C/AC/AP/ZTE 设备验证仍标记 `REAL_DEVICE_PENDING`。现有模拟器历史证据只作为
  旧 SSH.NET/DPAPI 路径的诊断记录，不能替代新 worker 的实机验收。

## 离线部署

部署包必须同时包含 CPython 运行时、`netmiko==4.7.0` 及其锁定依赖。服务优先使用显式环境变量，其次查找服务目录下 `tools/netmiko/` 中的离线包，最后查找 PATH 上的 Python。若 worker 不存在、依赖缺失或 driver
未安装，连接失败并保留安全错误码，不回退到另一套 SSH 客户端。

Windows x64 离线包的构建、校验与环境变量配置见 [离线部署](NETMIKO_OFFLINE_DEPLOYMENT.md)。
Service 离线候选已通过组合构建、隔离及搬迁测试，目标机需要 .NET 10 ASP.NET Core Runtime；正式安装与跨机验收仍待完成。
