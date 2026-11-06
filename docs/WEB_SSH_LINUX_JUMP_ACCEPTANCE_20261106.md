# Web 控制台、Linux SSH 与 Site Jump Host 增量验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。本批保留当前分支，只做本地提交；不 Push、Merge、Release，不修改真实数据。完整全天候目标仍为 `PARTIAL`。

## 实现

- 网络设备采集、连接测试和 Web Shell 继续统一使用 CPython + Netmiko。轻量服务只管理任务、凭据、驱动、Host Key、取消、错误和数据发布。交换机/路由器/AC/独立 AP 等共用此入口；FIT-AP 通过 AC 管理。
- 设备快速添加把连接资料、厂商/平台、Site、分组与库存记录放入同一事务；冲突或中途失败不留下半条设备。
- ZTE 页面提供新增、密码更新、留空保留、明确清除及采集操作，普通 DTO 不返回密码。
- Linux 服务器有独立目录、分组、连接状态及最近时间；WebSocket 传递真实 SSH ShellStream 键盘和输出，支持中文、ANSI、控制键、PTY resize、多标签、分屏、断开和重连。Linux 不继承网络设备的 Site Jump 策略。
- Site 设置有 SSH 中转开关、地址/端口/用户名/密码、保存、测试、重新获取指纹和删除指纹。运行状态来自后端配置和实际测试结果。中转按需建立并释放；网络 SSH 采集、连接测试和 Web Shell 复用 Netmiko 及 Paramiko `direct-tcpip`，Telnet/Ping/SNMP/UDP 不套用该中转。
- 首次及变化指纹均在认证成功后记录，目标设备、Linux 与 Jump 指纹分别保存；配置变更期间的旧连接结果不能覆盖新状态。密码属于设备或 Site 记录，不引入普通凭据 DPAPI 中心。
- 数据库密码为本机 SQLite 明文字段。Catalog 初始化在创建 token/数据库前收紧 Next 数据根权限：Windows 当前运行账号、SYSTEM、Administrators；Linux 目录 `0700`。已有显式文件权限不递归重写。自动指纹信任不构成独立设备身份验证。
- Windows 离线候选从当前 Vue 源码离线构建页面，并附带隔离 CPython、Netmiko 和锁定依赖；不会以旧的源目录静态文件替代当前 UI。

## 原版与界面参考

原版只读参考为 `D:\study\NetConsole-Workspace\NetConsole` 中的 `netmiko_connection.py`、`connection_manager.py`、`ssh_tunnel.py` 与 Host Key 服务。复用其 Comware/ZXROS 协议驱动、自动认证、命令交互和中转经验；没有把业务 parser、Capability 或 SQLite 搬进 worker。入口和差异清单见 [连接盘点](NETMIKO_CONNECTION_AUDIT_20261106.md)。

SmartHub 只读参考为 `D:\study\h3cai-smarthub\ui-reconstruction` 的布局、导航与设计 token；保留 Next 的 Vue 3/Element Plus 和既有业务，不迁移 AI 模块。Linux/中转表单及终端与现有布局和主题合并。

## 当前测试证据

| 层级 | 结果与边界 |
| --- | --- |
| Service Release build / full harness | PASS；包含 Linux/ACL、旧终端迁移、凭据/Jump、原子 onboarding、现有任务/Raw/权限回归 |
| Frontend | 类型检查 PASS；Vitest 8 files / 23 tests PASS；当前 Vue Vite 构建 PASS |
| Worker / 离线工具 | Netmiko worker 20 tests PASS；打包 4 + 候选组合 3 tests PASS |
| 网络终端完整 E2E | PASS；loopback SSH/Telnet/AUTO、中文、resize、重连、分屏、COM、ticket、历史/回放/导出；无效 resize 不再断开 WebSocket |
| Linux HTTP/WebSocket/SSH + Edge | 最后权限修复后完整复验 PASS；真实 loopback SSH 认证，CRUD、空密码保留、控制键/中文/ANSI、resize、多会话、两种重连、Site 隔离、指纹、并发限制、录制与重启/idle/delete；完全静默撤权后关闭 WS 并释放 SSH |
| Jump 真实转发与 Settings 浏览器 | `SITE_JUMP_HTTP_NETMIKO`、`SITE_JUMP_RESTART_DELETE` PASS：真实 loopback direct-tcpip、两 Site、采集/连接测试/Web Shell、并发释放/取消、指纹/凭据/错误分类、Telnet 绕过及重启；Chrome Settings mock API 页面门禁另 PASS |
| 最终离线候选运行 | `OFFLINE_CANDIDATE=PASS`；候选 EXE 从自身目录启动，清除 Python/worker 覆盖且 PATH 无系统 Python；内置依赖、HTTP 静态资源 hash、当前 Linux/Jump/Terminal 入口、Netmiko SSH 自动登录/命令及 Linux WS ANSI/中文/原始键盘/resize/close 均通过 |
| 授权 HCL 模拟器 | AC1/AC2/SW 通过当前 Netmiko 执行只读命令；FIT-AP 保持 AC 管理；配置写入 0 |

模拟器证据：Workspace `reports/netmiko-simulator-20261106.json`。AC1 `192.168.56.2:22`、AC2 `192.168.56.5:22` 使用 `hp_comware` 执行版本及 AC AP 查询；SW `192.168.56.3:22` 执行版本、接口摘要和 LLDP 查询。此证据确认登录和命令返回，不证明 AP 身份、实时归属、主备状态或 HA。历史 SW Exec 与 FIT-AP 独立 Telnet 结果保留为历史记录，不再作为当前连接路径的阻断。

最终候选路径：`D:\study\NetConsole-Next-Workspace\build\service-web-linux-jump-a3954a5-20261106`。程序集 ProductVersion 为 `1.0.0+a3954a5e95590782a576272d4e4b3c526eab248b`。包含隔离 CPython 3.13、Netmiko 4.7.0、Paramiko 4.0.0 和本批最新 Vue 页面。复验入口为 `py -3.13 -B tests/offline_candidate_e2e.py --candidate <上述目录>`；测试驱动 Python 只运行模拟器和 HTTP 客户端，候选服务使用自身运行包。候选 Service/worker 无残留，隔离临时数据已回收；未执行 Windows SCM 安装、升级或卸载。

## 保留边界

真实硬件、真实 Linux 主机、实际堡垒机及未验证型号均 `REAL_DEVICE_PENDING`。双 AC HA/切换/恢复与 AP 实时归属没有完成；没有进行远端配置变更。Web 仍限 loopback，跨机 Web/TLS/远端访问未实现。Windows 原生 125% 缩放及其他机器的安装/升级/卸载尚未验收；1920×1080/2K 浏览器视口不替代原生缩放。候选采用 framework-dependent .NET，需要目标机具备 .NET 10 ASP.NET Core Runtime。

正式 AP candidate/replacement、Domain/group 生命周期和剩余完整工作台能力仍在动态任务池；不得将本批连接与终端完成写成全产品完成。
