# 设备管理 Phase 1 验收记录

目标分支：`codex-A/next-device-management-phase1-20261026`

数据边界：验收使用临时隔离目录（例如 `D:\study\NetConsole-Next-Workspace\test-data\Device-Phase1Data` 或测试临时目录），不读取、不写入 `D:\NetConsoleData` 与 `D:\NetConsoleData-dev`。

## 自动化证据

| 项目 | 结果 |
|---|---|
| Service Release build | PASS（含 schema v11；NU1903 为既有依赖告警） |
| C# service harness | 已覆盖 schema v11、CRUD、分组、CSV create/update、敏感列拒绝、冲突回滚、profile link/history |
| Vue typecheck | 已通过 `vue-tsc --noEmit` |
| Vue Vitest | 已通过既有 3 files / 4 tests |
| Vite production build | 已通过；保留现有大 chunk warning |
| Git diff check | PASS（最终提交前） |
| GitHub Ubuntu/Windows CI | PASS：run `40272653066`（Ubuntu/Windows）；Windows syntax `40272653095` |

## 浏览器验收清单

- [x] SUPERADMIN 登录后看到设备列表、服务端分页、搜索、状态/分组筛选。
- [x] 新增设备后刷新仍存在；编辑名称/IP/描述后 deviceId 不变。
- [x] 终端档案入口保持可用；禁用/启用由服务端同步。
- [x] 详情显示终端档案、最近会话和设备审计；IP 编辑后既有历史仍可见。
- [x] 创建分组并把设备归组，分组筛选结果正确。
- [x] CSV 预览显示 create/update/conflict；含敏感列被拒绝；有冲突的提交不写入。
- [x] 无冲突 CSV 显式确认后原子导入，刷新显示新增/更新结果。
- [x] ADMIN 仍可读；写操作由服务端返回 403，不依赖前端隐藏按钮。

## 真实设备边界

本验收不连接真实 SSH/Telnet/Serial 设备，不执行真实采集，不使用生产或长期开发数据。真实设备连通性、Windows 服务安装和 Desktop Helper 由既有验收门单独负责。隔离目录 `D:\study\NetConsole-Next-Workspace\test-data\Device-Phase1Data` 已在验收结束后移除；浏览器截图保留在构建产物目录的 `D:\study\NetConsole-Next-Workspace\build\device-management-browser`。
