# R7.8 十工作流接续任务池
日期2026-11-09；产品HEAD4e1b09a。已完成批次见最终验收，不重复开发。各流可独立推进；核心文件由主集成协调，禁止远端Push/Merge/Release。
| 流 | 本批交付 | 下一具体任务 / 缺口 |
| --- | --- | --- |
| A FIT_AP_AND_DEVICE_INTEROP | more正文完整读取、官方SSH/Telnet60测试、双AC新只读Raw | FIT/SW有效现有凭据后单次认证及原生身份；不猜密 |
| B DUAL_AC_HA_AND_WIRELESS | 冻结R3资源/独立观察复用；AC1/2各6命令 | 可逆切换/回切与实际客户端连续性前提未齐，REAL_HA待验 |
| C CONFIG_BACKUP_RESTORE_ROLLBACK | 备份新鲜度提醒、变化事件、连续稳定恢复、比较/正文保护 | 全文备份与hash、设备传输/replace/回读/业务/失败回滚 |
| D FAULT_CENTER_AND_AUTO_DIAGNOSIS | 活动Ping闭环、分协议与暂停隔离、配置事件、中文状态/精确设备入口 | 模拟器受控故障注入；更多Syslog/接口证据不能推设备离线 |
| E LONG_PING_QUICK_PING_AND_NETWORK_DIAG | DNS实际地址、显式TCP入口、诊断服务端分页跨页选择 | 完整Host ICMP权限失败；TCP逐跳/吞吐独立业务合同 |
| F NETCONSOLE_LEGACY_FEATURE_MIGRATION | 更新全局/详细能力矩阵及实现与现场状态 | Mesh/MR/SNMP/专业报表按窄业务切片，复用现有协议 |
| G WINDOWS_DEPLOYMENT_LOCALMANAGER | 总截止时间、PID守卫、离线计划门禁 | SCM账户安装/升级/回滚/卸载/重启保留数据实际验证 |
| H TOPOLOGY_LLDP_AND_ASSET_MANAGEMENT | 有效历史/CSV/分页、伪链路过滤、并发4、部分失败保留资产 | 更大现场规模及权威客户端/AP关联，未知不猜测 |
| I UI_UX_AND_PERFORMANCE | 总览未知、账号隔离、精确设备详情、中文状态、两分辨率 | 继续实际运维流程和规模性能；125%明确不执行 |
| J INTEGRATION_REGRESSION_AND_LONG_RUN | Web450/Worker60/Service全量/发布/HTTP/浏览器/Raw完整性 | DLL完整门禁失败保留；8–24h、真实HA/恢复/SCM验收另补 |
所有后续按DISCOVER→IMPLEMENT→定向测试→集成→浏览器→本地commit→检查点继续。禁止把未验设备工作升级为PASS。
