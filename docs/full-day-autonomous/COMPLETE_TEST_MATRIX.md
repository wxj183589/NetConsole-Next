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
- Module registry API：隔离服务中普通 ADMIN 写入 403、SUPERADMIN 启停成功、核心模块停用 409、停用模块直接 API 404、重启后状态保留 PASS。
- R3 wireless persistence：schema v18、AC Domain/AP resource/Observation/AP Group 事务 upsert、稳定 ID、重复投影幂等和 Catalog 重启恢复 PASS。
- Cross-AC collection manifest：schema v19 manifest + item provenance、source revision、Raw manifest reference、跨 AC 计数和重启恢复 PASS。
- Cross-AC collection manifest read API/UI：Domain 列表、详情、source AC 过滤和当前 Site claims 聚合 PASS；`R3_DUAL_AC_COLLECTION_MANIFEST=PASS`、`R3_DUAL_AC_COLLECTION_MANIFEST_SCOPE=PASS`。
- R3 dual-AC API fixture：过滤、隐藏来源、MAC 校验和授权 guard PASS（`R3_WIRELESS_FILTER_SCOPE=PASS`）。
- 四台模拟器认证/HA：BLOCKED（无 DPAPI vault/profile；协议 banner 可达）。
- 生产/真实开发数据写入：0。
- Service-backed Browser smoke：隔离数据根、loopback 临时端口、1920×1080 登录与页面导航 PASS。
