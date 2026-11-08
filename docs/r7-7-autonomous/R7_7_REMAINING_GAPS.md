# R7.7 剩余缺口

总体PARTIAL；不将任何fixture或TCP可达写成真实设备闭环。

| 优先级 | 缺口 / 状态 | 下一项可执行工作 |
| --- | --- | --- |
| P0 | APPHOST ICMP权限 PARTIAL | r77-offline-apphost-final.log失败；同候选显式DLL完整门禁已过。调查进程上下文与Windows拦截来源，不修改全局安全策略 |
| P1 | FIT原生认证 BLOCKED | 需要已有有效凭据和AC exec-console有效读回；不猜密码，不以AC身份代替原生MAC/Serial |
| P1 | SW认证 BLOCKED | 已知配置失败，本轮未重复尝试；有新有效配置后再执行一次定向产品采集 |
| P1 | 整机配置恢复 BLOCKED | 历史SFTP/设备文件长度hash/native replace/重连/回滚证据缺失；有安全完整闭环后再准入写入 |
| P1 | HA/回切 NOT_TESTED | 完整前备份、窄范围触发、稳定回滚、AP状态与真实客户端业务证据齐备后进行 |
| P1 | Windows SCM NOT_TESTED | 目标服务账户下安装/启动/停止/升级/回滚/卸载保留数据；离线门禁不替代 |
| P1 | 8–24h LONG_RUNNING NOT_TESTED | 300秒观察仅短测；另开实际运行窗口并采集资源、SQLite、Raw和退出恢复 |
| P2 | 活动Long Ping故障投影 PARTIAL | 当前仅完成的新鲜诊断作事件证据；扩展前维护顺序、资源、有效样本、取消及恢复边界 |
| P2 | 配置变化自动告警 PARTIAL | 已有latest2变化检测/差异UI；后续复用故障去重与证据模型，不能把UNKNOWN当作正常或变化 |
| P2 | Syslog/多协议并行事件 PARTIAL | 当前健康投影只看设备最新匹配探测，不是所有协议资源完整覆盖 |
| P2 | 共享数据变化历史 PARTIAL | 当前AP有效变化为空，Long Ping完成历史为空；不伪造变化，实际窗口到达后验收导出 |
| P3 | 旧产品完整迁移 PARTIAL | 继续按既有FEATURE_MIGRATION_MATRIX挑选Mesh/报表/SSH中转等业务切片，不重复造协议 |

所有下一批继续使用本地提交；禁止Push/Remote Merge/Release Publish。保留19个原静态资产、其它worktree与未推送提交，不复制旧NetConsole数据根。
