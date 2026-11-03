# NetConsole-Next R2 基线 UI 审计

## 范围

- 基线仓库：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`
- 基线分支：`codex-A/netconsole-next-r2-device-management-ui`
- 基线 HEAD：`4f05d6e565aa7393c57954c2514d409169069aa8`
- 基线工作树：创建 R2 分支前 clean；未覆盖用户已有修改。
- 参考交接：`D:\study\h3cai-smarthub\ui-reconstruction\docs\netconsole-next-ui-reference\README.md`

## 基线结论

当前 Next 已有可用的设备库存、创建/编辑、详情、分组、CSV 预览导入、批量预览确认和终端档案引用链路，但仍是 R1 密度与平面布局。主要缺口如下：

| 区域 | 基线证据 | R2 判断 |
| --- | --- | --- |
| Workbench shell | 侧栏、header、tab 已存在，但普通工作台字号集中在 9–11px | 可读性不足 |
| 设备列表 | 服务端分页、搜索、状态/分组筛选、批量操作已存在 | 缺少明确作用域栏、厂商/类型筛选；请求提交没有最新响应保护 |
| Site scope | UI 固定传 `siteId=local`；后端无 Site 列表 API | 不能伪造跨局点树；需明确显示当前授权作用域 |
| 创建/编辑 | 设备身份、IP、厂商、型号、类型、分组、终端档案、启用状态已有 | FIT-AP 独立创建入口需关闭；认证引用说明需前置 |
| 详情 | 信息、终端历史、审计已有 | 缺少稳定身份/安全/能力边界分组 |
| 视觉证据 | 既有脚本未覆盖 R2 125% 视口和弹窗证据 | 需形成独立可重复证据脚本 |

## 基线验证

- `npm --prefix web run typecheck`：PASS
- `npm --prefix web run test -- --run`：PASS，3 files / 4 tests
- `npm --prefix web run build`：PASS
- 基线视觉证据：`D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\baseline\`
- 基线脚本遇到的 Windows 兼容问题：既有 `tests/web_ui_browser_e2e.mjs` 硬编码 `python3`，本轮未改动该既有脚本，新增 R2 脚本使用 Windows 可用的 `python` 和可解析的绝对静态根目录。

## 数据与安全边界

本审计只读取源码、文档和隔离 fixture；未读取或复制 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未访问真实设备，未执行网络扫描或真实登录。

