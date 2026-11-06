# R6.1 功能迁移状态

| 功能域 | 状态 | 本轮进展 | 剩余工作 |
| --- | --- | --- | --- |
| 工作台/无线汇总 | PASS_LOCAL | 真实 AC/AP、任务失败统计和故障隔离 | 完整告警/趋势源 |
| 智能配置 | PASS_LOCAL_DRY_RUN | 两模板、变量校验、命令预览、写入明确禁止 | 设备适配、批量执行、回滚 |
| 拓扑/资产 | PASS_LOCAL_READONLY | 真实库存、LLDP claims、scope/revision、部分失败 | 图形拓扑、资产关联和历史 |
| 配置备份/差异 | PASS_LOCAL | 备份、版本、差异及脱敏下载 | 现场型号兼容 |
| 配置恢复 | PARTIAL_SAFE_PREVIEW | 前端预览/dry-run 已接入 | writer、验证、失败回滚 |
| 设备连接/终端/任务 | PASS_LOCAL/PARTIAL | 既有本地门禁保留 | 真实目标互操作 |
| 无线双 AC/AP identity | PARTIAL | 既有投影、身份和运行态合同 | HA、切换、live ownership |
| 诊断/LLDP | PARTIAL | 既有 Ping/TCP/DNS/Traceroute/LLDP | ARP/路由/WLAN/Mesh 完整工作台 |
| 部署 | BLOCKED_EXTERNAL | Release build/静态资源构建 | 目标机安装、TLS、升级回滚 |
