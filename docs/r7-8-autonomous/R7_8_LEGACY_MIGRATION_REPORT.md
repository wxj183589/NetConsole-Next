# R7.8 原版能力迁移审计
日期2026-11-09；整体PARTIAL。
本次审计读取Next源代码与既有对照，不访问旧NetConsole真实数据根、不复制旧数据库或完整Site。

[全局矩阵](../FEATURE_MIGRATION_MATRIX.md)已纠正旧的“仅两条命令”和过期NOT_STARTED描述；[R7.8详细矩阵](R7_8_MIGRATION_MATRIX.md)逐项映射源码/API/UI/测试，区分EXISTING、IMPLEMENTED、PARTIAL、MISSING、BLOCKED与现场验证。
网络设备复用官方Netmiko Worker；Linux终端保持既有独立传输。没有另造协议栈、退役HistoryStore或远程Agent Controller。

本批实质迁移：活动Long Ping故障闭环、配置变化事件、备份提醒、诊断分页、DNS实际地址、LLDP有效变化历史与精确资产/设备详情。最终Web450、Worker60、Service全量及HTTP/浏览器证据见最终验收。
原版Mesh/MR/列车地面专业能力、SNMP、吞吐、TCP逐跳路径、全部专业Excel/报告、设备整机恢复/回滚未完成。不将通用设备/CSV工具写成全部专业功能迁移。
下一步按业务切片复用既有Service/parser/Raw模型；先解决当前真实设备与权限门禁缺口，再扩展专业分析。旧矩阵中的历史路径仅是参考，不是新输出目录。
