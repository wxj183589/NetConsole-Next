# 里程碑与验收门禁

## P0 采集底座（当前）
- 新仓库初始化、明确的隔离数据根、独立 Core 原型。
- SQLite v1 Schema、Raw 原始字节保真、UDP 本机接收、受限队列与计数。
- Web loopback 健康检查，不是可远程访问的业务 Web。
- 基本构建及端到端 smoke。未通过长期运行、断电和真实工业设备验证。

## P1 稳定与可靠采集
- 正式 Windows Service、受控本机 Manager、单实例和安全启停。
- 停机/掉电/磁盘满恢复、Raw 与 SQLite 对账、按源滚动分段和持久任务。
- H3C SSH/SNMP、AC/FIT-AP、Online MR、Syslog、长 Ping；真实采集回归。
- 2核4GB 与 4核8GB 72小时和7天实测，CPU、工作集、IO、丢包量、数据缺口需记录。
- Windows 10/11、8/8.1、Server2012/R2 单独兼容矩阵；非 Win10 系统不能以构建成功代替验收。

### G1 基础实现状态
- 已有启动对账：恢复 `.open`、补登未索引 `.ndjson`、验证行与 SHA-256、标记缺失段；损坏尾部保留完整前缀并记为 `INCOMPLETE`。
- 已有运行记录和单实例互斥；健康 API 暴露队列、收发计数、最近持久化时间、进程资源、磁盘可用空间和 Raw 状态计数。
- 已有 Windows Service 宿主适配、安装脚本和本机 Local Manager CLI；Windows CI 负责编译。
- 损坏 Raw 尾部在截断前先写入并校验 `recovery-evidence/` 不可变证据文件；集成测试验证字节与 SHA-256，并检查重复启动不会重复登记段或再次修改证据。
- **仍待验证：**Windows 服务真实安装/启停和权限、Windows Service 恢复策略、目标旧 Windows 兼容性、长时间掉电/磁盘满测试及 72 小时/7 天负载验收。Linux 自动测试不能替代这些目标环境验证。

## P2-P5
- P2：按成熟开源模式重新设计工具集和可选 HTTP/FTP/TFTP/WebDAV/SFTP Server。
- P3：实时轻量监测、RSSI/MESH 概览。
- P4：离线 MR/MESH 深度分析。
- P5：报告导出及轨道交通完整功能逐步接管。

旧 NetConsole 仍是生产软件；新版所有关键能力验收完成后才允许切换主线。
