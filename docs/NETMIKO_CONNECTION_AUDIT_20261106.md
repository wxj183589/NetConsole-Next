# Netmiko 连接盘点与后续工作

状态：`PARTIAL / REAL_DEVICE_PENDING`。本记录以当前源码和本批测试为准；历史 checkpoint 描述的是当时的状态。

## 原版参考

| 能力 | 主要位置 |
| --- | --- |
| SSH/Telnet 连接目标、协议尝试、超时和错误分类 | `src/netconsole/services/netmiko_connection.py`、`connection_manager.py` |
| H3C/ZTE SSH 与 Telnet 驱动 | `netmiko_connection.py` 的 `hp_comware`、`hp_comware_telnet`、`zte_zxros`、`zte_zxros_telnet` |
| 命令、分页和交互输出处理 | `netmiko_connection.py` 的 `safe_send_command` 和 `safe_send_command_with_paging` |
| SSH Host Key 和中转 | `host_key_trust_service.py`、`host_key_manager.py`、`ssh_tunnel.py` |
| 采集的命令选择和业务解析 | `device_operation_service.py`、`device_collection_support.py` 和各业务 parser |

原版实现有针对设备的编码与输出经验，不能仅凭驱动存在就宣称所有型号兼容。

## Next 入口与差距

| 入口 | 当前状态 |
| --- | --- |
| H3C 采集和设备连接测试 | 默认 Netmiko worker；密码按 H3C profile 读取，首次/变化的 Host Key 在认证后记录，包括旧 STRICT 档案 |
| SSH/Telnet Web Shell | 共用 worker 交互通道；显式允许的 AUTO 回退支持 SSH 协商失败和超时；SSH 支持远端 PTY 尺寸调整，Telnet 不支持远端调整 |
| ZTE 采集 | 走 Netmiko 的 `zte_zxros`，使用对应 Site 中转；独立 ZTE 档案保存读取密码并在认证后记录和轮换 Host Key；设备管理页已提供档案新增、凭据更新和采集入口，本机 API/组件回归通过 |
| Site Jump Host | Settings 提供完整配置、连接测试及指纹操作；已保存密码自动使用，成功后记录首次指纹及变化，防止配置变化期间写入旧结果；真实回环 direct-tcpip 转发、采集和 Web Shell 验收通过；Telnet 不套用 SSH 中转 |
| 驱动 | 由设备记录厂商和明确的 Cisco 平台选择；未知映射拒绝；映射和安装验证不是型号实机兼容证明 |
| 旧连接实现 | SSH.NET 的旧设备 transport 仍存在供显式测试和历史代码使用；当前 H3C、ZTE 默认采集及网络终端入口不使用它。中转转发和探测使用 Netmiko 依赖 Paramiko 的 SSH 通道 |
| 离线运行包 | Windows x64 CPython 3.13 隔离构建、搬迁校验及回环 Telnet 已验证；Service 候选组合构建通过，正式安装和其他机器验收待完成 |
| Linux 服务器与交互终端 | 独立 inventory 和 SSH.NET ShellStream；loopback Paramiko 模拟器 HTTP/WebSocket 及 Edge 浏览器 E2E 通过，含静默撤权后释放 SSH。Linux 真实主机仍待验收；服务仍限 loopback，跨机访问未实现 |

自动信任和轮换不提供独立的设备身份验证。Telnet 是明文传输；启用自动回退后，SSH 超时也会尝试配置的 Telnet 目标。

## 验证范围

完整 Service harness PASS；Vue 类型检查和 8 files / 23 tests PASS；`netmiko_worker_test.py` 20 项、离线打包单元测试 4 项、Service 候选组合测试 3 项 PASS。Windows 离线包隔离运行、搬迁清单和回环 Telnet 通过。网络终端浏览器测试使用 loopback 合成 SSH/Telnet 服务，覆盖登录、命令、中文、AUTO 回退、COM、分屏、resize、重连及录制回放。Linux 使用当前 Vue Webroot 和 loopback Paramiko 模拟器通过 HTTP/WebSocket 与 Edge 浏览器门禁；Jump 使用真实回环 SSH 转发通过 HTTP/Netmiko/WebSocket/重启门禁，Settings Chrome 浏览器另使用 mock API 验证界面。以上模拟环境证据不等同真实设备、真实 Linux 主机、现场网络或其他 Windows 环境验收。

2026-11-06 当前 Netmiko `hp_comware` 已对 HCL AC1/AC2 执行版本及 AC AP 查询，对 SW 执行版本、接口摘要和 LLDP 查询，均返回有效输出。证据位于 Workspace `reports/netmiko-simulator-20261106.json`。FIT-AP 保持 AC 管理，不要求独立登录；历史 SW Exec 与独立 FIT-AP Telnet 失败不再是当前路径阻断。双 AC HA、切换、恢复及 AP 实时归属仍待验证，远端配置写入为 0。最新批次验收与限制见 [Web/Linux/Jump 验收](WEB_SSH_LINUX_JUMP_ACCEPTANCE_20261106.md)。
