# NetConsole Next UI 真实浏览器验收

## 验收环境

| 项目 | 证据 |
| --- | --- |
| Worktree | `D:\NetConsoleNext-Integration` |
| Branch | `codex-A/next-ui-integration-20261026` |
| Integration base | `audit/p0-g1-stack-integration @ d07cdb4caaaf9942f560390279d407627037f613` |
| UI source SHA | `780870abe3e7b2c8430359388458b2fd055bd48b` |
| Service | `http://127.0.0.1:18772` |
| Browser | Windows Google Chrome `C:\Program Files\Google\Chrome\Application\chrome.exe` |
| Account | `admin/admin`，仅隔离预览 seed |
| Data root | `D:\NetConsoleNext-IntegrationData`（验收后已退役） |
| Device simulation | 未启用；无现场设备连接 |
| Required viewports | `1920x1080`、`1440x900`、`1366x768` |

`tests/ui_reference_browser_e2e.mjs` 启动真实 Chrome，登录真实 .NET Service，逐页访问并保存截图，同时捕获登录后的 console error、page error、失败请求、HTTP 500+ 和 401/404。这次历史验收的原证据路径为 `web/artifacts/ui-acceptance/browser-evidence.json`；当前脚本重新运行时改为 `D:\study\NetConsole-Next-Workspace\build\ui-acceptance\browser-evidence.json`（或配置的工作区根目录下 `build\ui-acceptance\`）。若设置 `NETCONSOLE_NEXT_UI_ARTIFACTS`，只接受工作区 `build` 内的覆盖路径。

## 截图交付

截图均来自真实 Service，不是静态 Mock HTML 或生成效果图。下列是历史验收的原始相对路径，用于追溯旧证据；当前重新运行的同名截图统一输出到 `D:\study\NetConsole-Next-Workspace\build\ui-acceptance\`：

- `web/artifacts/ui-acceptance/overview-light.png`
- `web/artifacts/ui-acceptance/overview-dark.png`
- `web/artifacts/ui-acceptance/devices-light.png`
- `web/artifacts/ui-acceptance/devices-dark.png`
- `web/artifacts/ui-acceptance/terminal-dark.png`
- `web/artifacts/ui-acceptance/tasks.png`
- `web/artifacts/ui-acceptance/tools.png`
- `web/artifacts/ui-acceptance/files.png`
- `web/artifacts/ui-acceptance/settings.png`
- `web/artifacts/ui-acceptance/overview-1440x900.png`
- `web/artifacts/ui-acceptance/overview-1366x768.png`

截图内容使用隔离空 Catalog：设备列表为 `0`，终端页面无可用终端设备，趋势和告警显示真实空状态。截图中的任务、服务健康、资源采样来自真实 Service 状态，不是硬编码视觉示例。

## 对照表

| 项目 | 目标参考结构 | 实际结果 |
| --- | --- | --- |
| 全局导航 | 左侧紧凑导航、顶部状态栏、工作区 | 已实现，浅色与深色共用结构 |
| 运行总览 | KPI、状态分布、告警、趋势、资源 | 已实现；无数据区域显示解释性空态 |
| 设备管理 | 搜索、筛选、紧凑表格、分页、详情 | 已实现；当前隔离库真实显示空库 |
| 终端工作台 | 左设备栏、中连接栏、右终端区、底部历史 | 已实现；保留真实 xterm/session/replay/export 逻辑 |
| 任务中心 | 状态和进度 | 复用真实 Task API；无任务时不制造进度 |
| 工具集 | 工具卡片、权限和启动入口 | 复用既有 Desktop Helper/SUPERADMIN 约束并统一卡片样式 |
| 文件管理 | 根目录、面包屑、文件表、下载和 ZIP | 复用既有文件/归档 API 并统一页面工作区 |
| 系统设置 | 用户、配置、凭据、数据、关于 | 复用既有权限和设置边界并统一布局 |

## 匹配项与差异

### MATCHED_ELEMENTS

- 侧边导航、顶部搜索/状态栏、工作区标签和页面标题层级与运行总览参考图一致。
- 浅色管理主题、深色主题和深色终端区共享同一套品牌蓝、状态色、卡片圆角和紧凑间距。
- 终端页面实际呈现三栏加底部历史结构；没有设备时仍保留工作台骨架和真实空态。
- 设备页提供搜索、状态筛选、分页、详情和数据质量反馈，不再把整库一次性渲染到页面。

### VISUAL_DIFFERENCES

- 参考图含有丰富现场设备、告警、RSSI 曲线和终端输出，本次隔离库没有这些真实数据，因此截图显示空态；没有使用假数据填充。
- 参考图中的部分图标、具体文案和面板比例是视觉基准，当前实现使用 Element Plus 图标和现有业务组件，保持信息层级但不是逐像素复制。
- 工具、文件、任务和设置页主要通过统一 shell/theme 对齐，未在本任务中虚构新的业务能力。

### FUNCTIONAL_DIFFERENCES

- `NETCONSOLE_NEXT_TERMINAL_ENABLED` 未打开，浏览器验收未连接 SSH、Telnet 或 COM 现场设备；因此仅验证工作台结构、真实 API 空态、页面错误捕获和既有终端代码仍可构建。
- 真实设备、无线管理和轨道交通业务数据不在隔离预览 Catalog 中，本次不以空态截图宣称现场采集验收完成。

### REMAINING_GAPS

- 需要在用户批准的非生产设备数据集上再次验收 782 条历史数据的来源与字段质量；当前预览 DB 无法追溯该数字。
- 需要在具备真实设备与启用终端功能的环境补做连接、录制、回放、导出和断线重连验收。
- 当前 Vite 仍提示 Dashboard/入口 chunk 较大，属于已有前端打包优化项，不阻塞本次视觉验收。

## 最近一次结果

`browser-evidence.json` 记录：

```text
viewportChecks: 1920x1080, 1440x900, 1366x768
screenshots: 11
consoleErrors: 0
pageErrors: 0
failedRequests: 0
badResponses: 0
authOrMissingResponses: 0
deviceRows: 0（API `total=0`，页面显示真实空态）
```

自动化状态为 `UI_INTEGRATION_VERIFIED`（审计基线上的 UI 集成和隔离预览验收通过）；现场设备连接、782 条历史数据追溯和无线/轨交业务验收仍按上面的剩余项单独进行。
