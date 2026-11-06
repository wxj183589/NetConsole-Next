# R6 任务池

| 优先级 | 任务 | 状态 | 证据/下一步 |
| --- | --- | --- | --- |
| P0 | 工作台真实设备、任务、无线汇总 | COMPLETED_LOCAL | 真实 AC Domain/AP Resource API、失败任务统计、无线错误隔离；Web 211 tests、typecheck/build PASS |
| P0 | 原版设备/连接 parity | PARTIAL | 设备页和 Netmiko 已有；补现场/模拟器证据 |
| P0 | 无线 AC/AP 与双 AC | PARTIAL/BLOCKED_EXTERNAL | projection/identity 本地通过，HA 与凭据阻断 |
| P1 | 资产/LLDP/拓扑工作台 | PARTIAL | 复用 H3cLldpTopology 与 Site scope |
| P1 | 智能配置 | NOT_IMPLEMENTED | 先做模板变量和 dry-run 合同 |
| P1 | 智能配置 dry-run | COMPLETED_LOCAL | 两个 H3C 模板、变量/注入校验、命令预览；不创建任务、不连接设备、不写入 |
| P1 | 配置恢复 writer | BLOCKED_EXTERNAL | 前端已接入预览/dry-run；当前默认拒绝设备写入 |
| P1 | 拓扑/资产工作台 | COMPLETED_LOCAL | 真实库存 + LLDP claims、scope/revision、错误/空/部分状态；完整图形拓扑待做 |
| P1 | 终端/跳板真实互操作 | PARTIAL | 本地 session 通过，目标机待验 |
| P2 | 安装、TLS、升级回滚 | BLOCKED_EXTERNAL | 生成离线候选后再做目标机门禁 |
