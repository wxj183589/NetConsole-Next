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

worker 连接后先返回 SSH SHA256 指纹；.NET 通过现有 HostKey policy 自动首次信任或轮换，
不弹出确认窗口。Telnet 不使用 HostKey。现有 `SshNetH3cSshTransport` 仅保留为显式
合成 wire test transport，不是生产默认入口。

## 凭据

H3C profile 的密码存于设备 profile 记录，按 profile ID 读取；共享或重复的
`CredentialReference` 不会导致设备间串用。创建时输入密码，后续测试、采集和终端会话自动
读取；留空更新保留旧值，明确 `clearPassword` 才清除。当前 SQLite 字段是本机受 ACL 保护的
明文字段，尚未宣称数据库级加密。HostKey 与登录密码分开管理。

## 当前边界

- 已完成 worker、驱动映射、默认采集接入、凭据按设备读取和离线回归。
- Web Shell 的交互终端仍在现有 Shell/Telnet transport 上运行，后续应复用 worker 的
  Netmiko interactive channel 或由实机证据决定保留独立交互 transport。
- Site Jump Host 已完成 Site 级持久化、脱敏 API、一次性 Netmiko worker 探测和显式指纹刷新：
  `POST /api/system/sites/{siteId}/ssh-jump-host/test` 对已保存指纹变化 fail-closed，
  `POST /api/system/sites/{siteId}/ssh-jump-host/refresh-fingerprint` 才会更新 Jump Host
  指纹；首次为空指纹的生产连接在得到可验证指纹后写回 Catalog。Telnet 不套用 SSH Jump Host。
  Linux 服务器 inventory 和跨机 WebSocket 仍未实现；不得把这些能力写成已验收。
- 真实 H3C/AC/AP/ZTE 设备验证仍标记 `REAL_DEVICE_PENDING`。现有模拟器历史证据只作为
  旧 SSH.NET/DPAPI 路径的诊断记录，不能替代新 worker 的实机验收。

## 离线部署

部署包必须同时包含 CPython 运行时、`netmiko==4.7.0` 及其锁定依赖，并把
`NETCONSOLE_NEXT_NETMIKO_PYTHON` 指向离线 Python。若 worker 不存在、依赖缺失或 driver
未安装，连接失败并保留安全错误码，不回退到另一套 SSH 客户端。
