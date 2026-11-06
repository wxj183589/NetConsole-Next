# R6 下一任务池

| 优先级 | 任务 | 状态 | 下一步/验收 |
| --- | --- | --- | --- |
| P0 | 工作台真实无线与任务汇总 | COMPLETED_LOCAL | Dashboard 使用真实 AC/AP API；2/2 定向测试及 Web 全量测试通过 |
| P1 | 智能配置 dry-run | COMPLETED_LOCAL | H3C 接口描述/Access VLAN 模板、变量校验、命令预览；5/5 C# + 2 Vue tests |
| P1 | 拓扑与资产工作台 | COMPLETED_LOCAL_READONLY | 真实库存和 LLDP claims；加载/空/错误/部分失败；4 Vue tests |
| P1 | 配置恢复预览和 dry-run | COMPLETED_LOCAL_SAFE | 配置备份页接入 preview/execute API；40 Vue tests，409 writer 阻断明确显示 |
| P0 | AC/AP 无线完整工作台与双 AC 运行态 | PARTIAL | 保持 Site scope 和 UNKNOWN/CONFLICT；补实际 AP ownership 与 HA 证据 |
| P1 | 配置恢复 writer、失败回滚 | BLOCKED_EXTERNAL | 现有预览 fail-closed；等待授权 writer 合同和现场恢复验收 |
| P1 | 智能配置设备适配和批量执行 | NOT_IMPLEMENTED | 在 dry-run 稳定后定义白名单写入、差异验证和回滚 |
| P1 | LLDP/拓扑/资产工作台 | PARTIAL | 复用现有 API 和 verified facts，补页面交互及空/错误状态 |
| P2 | 跨机器 TLS、安装、升级回滚 | BLOCKED_EXTERNAL | 目标 Windows 环境可用后执行部署门禁 |

完整历史任务与验收依据见 `docs/full-day-autonomous/NEXT_TASK_BACKLOG.md`。
