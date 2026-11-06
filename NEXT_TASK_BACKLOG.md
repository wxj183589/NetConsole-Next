# R6 下一任务池

| 优先级 | 任务 | 状态 | 下一步/验收 |
| --- | --- | --- | --- |
| P0 | 工作台真实无线与任务汇总 | COMPLETED_LOCAL | Dashboard 使用真实 AC/AP API；2/2 定向测试及 Web 全量测试通过 |
| P0 | AC/AP 无线完整工作台与双 AC 运行态 | PARTIAL | 保持 Site scope 和 UNKNOWN/CONFLICT；补实际 AP ownership 与 HA 证据 |
| P1 | 配置恢复 writer、失败回滚 | BLOCKED_EXTERNAL | 现有预览 fail-closed；等待授权 writer 合同和现场恢复验收 |
| P1 | 智能配置模板和校验 | NOT_IMPLEMENTED | 先实现无写入 dry-run、变量校验和设备适配测试 |
| P1 | LLDP/拓扑/资产工作台 | PARTIAL | 复用现有 API 和 verified facts，补页面交互及空/错误状态 |
| P2 | 跨机器 TLS、安装、升级回滚 | BLOCKED_EXTERNAL | 目标 Windows 环境可用后执行部署门禁 |

完整历史任务与验收依据见 `docs/full-day-autonomous/NEXT_TASK_BACKLOG.md`。
