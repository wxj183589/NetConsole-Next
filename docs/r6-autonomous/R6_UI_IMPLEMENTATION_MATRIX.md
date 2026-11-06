# R6 UI 实施矩阵

SmartHub 仅作为 clean-room 结构参考。当前实现必须通过 `NcLayout`、`moduleRegistry`、现有 API client、auth 和 Site scope；不复制专有 Logo、字体、sprite、bundle 或业务代码。

| 页面/工作包 | 当前状态 | 真实 API | 主要缺口 |
| --- | --- | --- | --- |
| 工作台 | PARTIAL | health、inventory、tasks、wireless domains/resources | 告警源、趋势源和完整资产汇总 |
| 设备管理 | PARTIAL | inventory、sites、groups、batch、connection tests | 现场互操作、资产批量闭环 |
| 无线业务 | PARTIAL | AC domains、AP resources、groups、observations、runtime | 实时多 AC、双 AC 和 ownership |
| 任务中心 | PASS_LOCAL | tasks、task detail、diagnostics、retry/cancel | 现场恢复证据 |
| 终端控制台 | PARTIAL | terminal sessions、SSH/Telnet/Linux | 真实跳板和目标机 |
| 配置备份/恢复 | PASS_LOCAL/PARTIAL | backup snapshots/diff/schedules、restore preview | writer 和回滚 |
| 工具/拓扑 | PARTIAL | diagnostics、traceroute、LLDP | ARP/路由/WLAN/Mesh 诊断视图 |

必须显式显示 `UNKNOWN`、`CONFLICT`、`NOT_COLLECTED`、`DISABLED`、`NOT_IMPLEMENTED`；空数据和 API 失败不能被渲染成绿色零值或 fake record。关键页面后续需补 1920x1080、2560x1440 的浏览器状态证据。
