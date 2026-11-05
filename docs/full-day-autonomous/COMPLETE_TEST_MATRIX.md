# 完整测试矩阵

- .NET Service build：PASS。
- Service test build：PASS。
- Service harness：PASS（既有全量回归；新增多命令定向 `11/11`）。
- Concurrency：`4/4 PASS`。
- Frontend typecheck：PASS。
- Vitest：`5 files / 10 tests PASS`。
- Browser：1920×1080、2560×1440 登录、主题、设备、终端、任务重试、工具、文件、未知路由、禁用模块 fixture PASS。
- Web contract：`4 checks PASS`；任务取消/重试/恢复 API 的 SUPERADMIN 与 Site scope 静态边界 PASS。
- Service-backed browser smoke：真实隔离 Service、真实登录、概览/设备/任务/工具/文件页面，1920×1080 PASS；临时数据根与服务进程已清理。
- R3 dual-AC API fixture：过滤、隐藏来源、MAC 校验和授权 guard PASS（`R3_WIRELESS_FILTER_SCOPE=PASS`）。
- 四台模拟器认证/HA：BLOCKED（无凭据）。
- 生产/真实开发数据写入：0。
