# 双 AC 1+1 模拟器报告

日期：2026-11-06。最新状态：**PARTIAL**。当前证据为 `D:\study\NetConsole-Next-Workspace\reports\netmiko-simulator-20261106.json`，路径为 CPython + Netmiko `hp_comware`。

| 对象 | 当前证据 | 状态 |
| --- | --- | --- |
| AC1 `192.168.56.2:22` | 自动 SSH 认证，`display version`、AP 列表、地址及 radio 查询 | PASS（只读命令） |
| AC2 `192.168.56.5:22` | 自动 SSH 认证，`display version`、AP 列表、地址及 radio 查询 | PASS（只读命令） |
| SW `192.168.56.3:22` | 自动 SSH 认证，版本、接口摘要及 LLDP 查询 | PASS（只读命令） |
| FIT-AP | 通过 AC 管理，不创建独立 SSH/Telnet 连接 | AC_MANAGED |
| 双 AC HA、切换/恢复、实时 AP 归属 | 未执行，不能从登录或命令返回推断 | PENDING |

配置写入为 0，未重启或切换设备。当前密码按设备 Profile 自动读取；旧 DPAPI、SW Exec 和独立 FIT-AP Telnet 失败属于历史路径，不再作为当前连接阻断。

模拟器命令返回不等于 AP 物理身份、业务端到端采集、主备状态或 HA 已验收。真实硬件及未验证型号仍标记 `REAL_DEVICE_PENDING`。
