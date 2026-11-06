# R6 实施矩阵

| 能力 | 状态 | 当前证据 | 剩余工作 |
| --- | --- | --- | --- |
| SmartHub clean-room UI 体系 | PARTIAL | NcLayout、组件和既有浏览器验收 | 本轮无全页面视觉回归 |
| 工作台 | PARTIAL | 真实 health/inventory/tasks/AC/AP API；本轮 202 Web tests | 告警、趋势和采集结果源未完整 |
| 设备/Netmiko/终端/任务 | PASS_LOCAL | 既有 service、HTTP、worker 和浏览器报告 | 真实目标设备互操作仍待验 |
| AC/AP 与双 AC | PARTIAL | 隔离数据投影、身份与无线 UI 报告 | HA、切换、实时 ownership 未验 |
| 配置备份/差异/恢复 | PARTIAL | 读取、差异和恢复预览已实现 | writer 故意 fail-closed，回滚未验 |
| 网络诊断/LLDP | PARTIAL | 已有诊断、Traceroute、LLDP 本地测试 | ARP/路由/WLAN/Mesh 完整工作台缺失 |
| 智能配置 | NOT_IMPLEMENTED | 无完整产品闭环 | 模板、变量、校验和任务闭环 |
| 安全/审计/数据边界 | PASS_LOCAL | 现有 Site scope、claims 和审计测试 | 目标机部署安全验收待做 |
| 安装/TLS/升级回滚 | BLOCKED_EXTERNAL | 本机离线候选历史门禁 | 跨机器及安装生命周期待验 |
