# R2 设备列表验收

## 已实现

- 当前授权局点以 `siteId` 显式展示，默认沿用既有 `local` scope；没有 Site 列表 API 时不扩展或猜测授权范围。
- 真实 `device-groups` 显示为作用域栏，支持全部设备和分组切换。
- 搜索、状态、厂商、设备类型、分组筛选均重新回到第 1 页，并通过服务端分页查询。
- 设备表保留稳定设备 ID、管理 IP、厂商/型号、类型、局点/分组、终端档案、更新时间和操作列。
- 分页保留 20/50/100；批量选择继续保存明确的 device ID，不依赖行号。
- 页面 loading、unsupported、空结果、数据质量异常状态继续保留。
- 旧响应不会覆盖新一次的 inventory/group/profile 结果。

## 验收证据

- `tests/r2_device_management_ui_visual.mjs`
- `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\final\devices-1920x1080.png`
- `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\final\devices-2560x1440.png`
- `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\final\devices-windows-125pct-simulated.png`
- `npm --prefix web run typecheck`：PASS
- `npm --prefix web run test -- --run`：PASS，5 tests

## 未覆盖

没有把真实多 Site claims、现场设备数量或网络侧 inventory 当作本轮 fixture。真实授权范围和真实设备 acceptance 留到 R7/授权环境。

