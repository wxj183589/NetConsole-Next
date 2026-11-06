# R6 下一步自主开发建议

1. 将无线 AC/AP 只读 API 接入更完整的业务工作台，呈现 AC、AP 组、AP 详情、运行态和采集历史，并补浏览器覆盖。
2. 在授权凭据可安全注入后，执行四台模拟器的只读登录和双 AC 前后状态证据；没有凭据时保持 BLOCKED_EXTERNAL。
3. 扩展智能配置模板、设备适配、批量任务和差异验证；任何设备写入继续受权限、预览和回滚门禁约束。
4. 完成配置恢复 writer 的白名单命令、重新采集验证、失败回滚和现场证据。
5. 完成拓扑图形展示、资产关联和 ARP/路由/WLAN/Mesh 诊断工作台。
6. 在目标机可用后验收跨机器 TLS、Windows Service 安装、升级和回滚。

总体优先级与依赖见 `NEXT_TASK_BACKLOG.md` 及 `docs/r6-feature-parity/FEATURE_MIGRATION_PLAN.md`。
