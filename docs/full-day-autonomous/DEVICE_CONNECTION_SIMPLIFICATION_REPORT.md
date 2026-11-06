# 设备连接简化报告

日期：2026-11-06。状态：COMPLETED_LOCAL，真实硬件兼容性 `REAL_DEVICE_PENDING`。

网络设备连接测试、H3C/ZTE 采集和网络 Web Shell 统一使用 CPython + Netmiko。快速添加填写 IP、协议、端口、用户名和密码，设备库存与连接 Profile 在同一事务中保存；后续测试、采集和终端自动按 Profile 读取密码。默认端口 SSH 22、Telnet 23，驱动使用显式厂商/平台/协议映射，未知型号不静默回退。

浏览器接收用户输入的密码，但普通查询与保存响应不返回已保存明文。密码存于本机 SQLite 字段，数据根使用收紧的文件系统 ACL；普通设备不使用 DPAPI 凭据中心。密码不进入 URL、命令行、日志、Raw 或导出报告。更新时留空保留，明确清除才删除密码；Host Key 与密码分别管理。

首次 SSH 指纹及变化在认证成功后自动保存，不弹确认窗口；旧 STRICT 档案也采用此自动策略。Telnet 自动认证并保留明文传输提示。

当前 HCL Netmiko 证据：AC1、AC2、SW 自动登录和只读命令 PASS；FIT-AP 为 AC 管理对象，不独立连接。证据为 Workspace `reports/netmiko-simulator-20261106.json`，配置写入 0。历史 SW Exec 和 FIT-AP 独立 Telnet 失败不再作为当前架构阻断；本结果不代表双 AC HA 或全部业务采集验收。
