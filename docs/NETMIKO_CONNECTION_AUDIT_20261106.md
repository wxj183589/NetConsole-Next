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
| ZTE 采集 | 走 Netmiko 的 `zte_zxros`，使用对应 Site 中转；独立 ZTE 档案保存读取密码并在认证后记录和轮换 Host Key，界面仍待完善 |
| Site Jump Host | 已保存密码自动使用；成功后记录首次指纹及变化，并防止配置变化期间写入旧结果；Telnet 不套用 SSH 中转 |
| 驱动 | 由设备记录厂商和明确的 Cisco 平台选择；未知映射拒绝；映射和安装验证不是型号实机兼容证明 |
| 旧连接实现 | SSH.NET 的旧设备 transport 仍存在供显式测试和历史代码使用；当前 H3C、ZTE 默认采集及网络终端入口不使用它。中转转发和探测使用 Netmiko 依赖 Paramiko 的 SSH 通道 |
| 离线运行包 | Windows x64 CPython 3.13 隔离构建、搬迁校验及回环 Telnet 已验证；Service 候选组合构建通过，正式安装和其他机器验收待完成 |
| Linux 服务器和跨机 WebSocket | 尚未完成；网络设备工作流不能代替 Linux SSH 验收 |

自动信任和轮换不提供独立的设备身份验证。Telnet 是明文传输；启用自动回退后，SSH 超时也会尝试配置的 Telnet 目标。

## 验证范围

`netmiko_worker_test.py` 20 项、离线打包单元测试 4 项、Service 候选组合测试 2 项；Windows 离线包隔离运行、搬迁清单和回环 Telnet 通过。浏览器测试使用 loopback 合成 SSH/Telnet 服务，覆盖登录、命令、中文、AUTO 回退、COM、分屏及录制回放。设备协议兼容、真实设备、现场网络和其他 Windows 环境仍待验证。

此前 AC1/AC2 模拟器登录证据来自旧连接路径，SW 的 Exec 和 FIT-AP 凭据仍未闭环；不能作为本次 Netmiko 设备验收。双 AC HA、切换和 AP 实时归属仍待验证，未对设备执行配置变更。
