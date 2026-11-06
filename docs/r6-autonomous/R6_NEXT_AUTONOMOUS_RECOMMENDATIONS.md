# R6 下一步自主开发建议

1. 将无线 AC/AP 只读 API 接入独立业务工作台，完整呈现 AC、AP 组、AP 详情、运行态和采集历史，并为加载、空、部分失败及 UNKNOWN 状态补浏览器覆盖。
2. 完成资产/LLDP/拓扑工作台，严格复用 Site scope 和 verified facts，不以缺席推断关系终止。
3. 建立智能配置模板、变量、校验和无写入预览；任何设备写入继续受权限、预览和回滚门禁约束。
4. 在设备及目标机可用后，分别验收双 AC 切换/恢复、配置恢复和跨机器 TLS/安装生命周期。

总体优先级与依赖见 `NEXT_TASK_BACKLOG.md` 及 `docs/r6-feature-parity/FEATURE_MIGRATION_PLAN.md`。
