# NetConsole Next 开发边界

- 优先级：P0 系统稳定性 > P1 可靠采集和原始数据 > P2 工具集 > P3 实时分析 > P4 离线分析 > P5 报告。
- Windows Intel/AMD x64 工业 mini 主机优先；旧 Windows、Linux、麒麟、ARM64 均须实机验证。
- 新架构：长期运行的无界面采集服务 + Web UI + 可选 Local Manager。Local Manager 只管理本机服务与目录。
- 默认单进程、模块化单体。初期组件独立维护、统一发布，避免微服务/插件市场。
- 不开发远程 Agent Controller。HTTP/FTP/TFTP/WebDAV/SSH-SFTP Server 默认不开启。
- SQLite + 分段原始 Raw 数据；新 schema 为准，禁止在旧 NetConsole 数据根上运行和修改其数据库。
- 不宣称 UDP 绝对不丢包。队列上限、故障诊断、受控退出、分段状态及完整性需要明确验证。
- UI、图表、导出均不得阻塞采集，只有完成真实测试才可以声称稳定/兼容。
- Web 默认仅绑定 loopback，任何远程管理须先实现用户认证、授权、TLS、操作审计。
