# NetConsole Next Windows 本地 UI 预览验收

日期：2026-10-26

任务：`NETCONSOLE_NEXT_SAFE_MAIN_INTEGRATION_AND_LOCAL_PREVIEW`

仓库：`D:\NetConsole-Next-Preview`

服务地址：`http://127.0.0.1:18771`

## 1. 预览来源与边界

本次预览目标对应 GitHub 远端审计栈：

```text
UPSTREAM_PREVIEW_BRANCH=audit/p0-g1-stack-integration
UPSTREAM_PREVIEW_HEAD=d07cdb4caaaf9942f560390279d407627037f613
```

Windows 终端 Git fetch/HTTPS 连接在本任务期间失败（连接 GitHub 443 被重置），因此没有声称本地工作树是该 SHA 的精确 checkout。为完成本地预览，使用 GitHub 只读补丁按 PR17、PR18、PR19 顺序将远端内容物料化到隔离工作树：

```text
LOCAL_PREVIEW_BRANCH=codex-A/next-local-preview-20261026
LOCAL_PREVIEW_HEAD=293abdf4fbbfb47af4827d6445624f9fe531cbe8
SOURCE_MATERIALIZATION=GitHub patch reconstruction; not an exact Git checkout of UPSTREAM_PREVIEW_HEAD
```

该工作树不是主仓库 `D:\study\NetConsole-Workspace\NetConsole`，也未对主分支、任何 PR 或远端引用执行写操作。

预览运行边界：

- 数据根固定为 `D:\NetConsoleNext-PreviewData`，不得指向 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`。
- Web 仅绑定 `127.0.0.1`；LAN、Syslog、H3C 和真实 Terminal 默认关闭。
- 不连接真实设备，不生成伪造无线、轨旁 AP、MR 或现场业务数据。
- 种子账号只适用于此隔离预览：`admin/admin`、`superadmin/superadmin`。
- 当前运行实例 PID 由 `preview-logs\preview-state.json` 记录；停止脚本只停止自己记录且启动时间匹配的进程。

## 2. 启动、检查与停止

```powershell
cd D:\NetConsoleNext-Preview
.\tools\local-preview\Start-NetConsoleNext-Preview.ps1 -NoBrowser
.\tools\local-preview\Test-NetConsoleNext-Preview.ps1
.\tools\local-preview\Stop-NetConsoleNext-Preview.ps1
```

使用 `-SkipBuild` 只适用于当前 Release Service 和 `wwwroot` 已确认是最新内容的情况。服务标准输出、错误输出和状态文件位于：

```text
D:\NetConsoleNext-PreviewData\preview-logs\
```

## 3. UI 页面范围

| 页面 | 预览状态 | 说明 |
| --- | --- | --- |
| 登录 | 已实现 | Vue 登录页；已做真实 HTTP 登录检查 |
| 运行总览 | 已实现 | 读取真实 health/task 快照；曲线无数据时保持为空 |
| 设备管理 | 已实现 | 读取 Catalog；不生成虚假设备 |
| 任务中心 | 已实现 | 展示任务状态；H3C 未开启时不宣称有真实采集结果 |
| 内置终端 | 已实现 | Vue/xterm 多标签结构；本预览禁用真实终端连接 |
| 工具集 | 已实现 | 宿主工具登记/状态结构；不自动启动外部程序 |
| 文件管理 | 已实现 | 逻辑根、列表、单文件下载和 ZIP 任务 |
| 系统设置 | 已实现 | 设置与密码入口结构 |
| 无线业务 | 禁用占位 | 尚未实现，不作为业务完成证明 |
| 轨道交通业务 | 禁用占位 | 尚未实现，不作为业务完成证明 |

## 4. 已取得的证据

| 检查 | 结果 | 证据边界 |
| --- | --- | --- |
| `npm ci` | PASS | 依赖可安装；npm 报告 10 个漏洞（4 moderate、3 high、3 critical），未隐藏 |
| `npm run typecheck` | PASS | TypeScript 类型检查 |
| `npm run test` | PASS | 2 个文件、2 个测试 |
| `npm run build` | PASS | Vite 产物写入 Service `wwwroot`；存在大 chunk 警告 |
| `dotnet build ... -c Release` | PASS | 0 errors；有 SQLitePCLRaw `GHSA-2m69-gcr7-jv3q` NU1903 警告 |
| Service C# 集成/性能测试 | PASS | 调度、解析、Raw、Catalog、恢复、SQLite/权限故障注入等通过 |
| `web_ui_contract.py` | PASS | 3 项 |
| `web_pr2_contract.py` | PASS | PR2 合约检查 |
| `terminal_ui_contract.py` | PASS | 3 项 |
| `smoke.py` | PASS | health、UDP 二进制保真、Raw seal、SQLite Catalog |
| Windows 实际 Service | PASS | health、home、Vue asset 均 HTTP 200 |
| Windows 实际登录 | PASS | `admin/admin`：`/api/auth/login` 200，`/api/auth/me` 200，角色 `ADMIN` |
| 页面路由 HTTP 检查 | PASS | `/`、`/login`、`/devices`、`/terminal`、`/tasks`、`/tools`、`/files`、`/settings`、`/wireless`、`/rail` 均 200 |
| `g1_recovery.py` | NOT RUN | 测试脚本明确要求 Linux，Windows 未冒充通过 |
| Playwright 实际浏览器 | PENDING | 本机没有 Chromium 可执行文件；下载浏览器未完成，未使用 mock 截图 |
| `web_auth_e2e.py`、`web_pr2_e2e.py` | BLOCKED_TEST_HARNESS | 现有脚本将 Service stdout 写入未消费的 PIPE 后挂起；未将其记为产品失败 |
| 截图 | PENDING_LOCAL_RUN | 未生成 mock 截图；需本机浏览器可用后重新执行真实 UI 验收 |

## 5. 验收结论

```text
LOCAL_SERVICE_READY=YES
AUTOMATED_BUILD_AND_CONTRACT_EVIDENCE=PASS
REAL_HTTP_LOGIN_EVIDENCE=PASS
WINDOWS_BROWSER_VERIFIED=NO
SCREENSHOT_EVIDENCE=PENDING_LOCAL_RUN
DEVICE_CONNECTION=NO
PRODUCTION_DATA_MUTATION=NO
MAIN_CHANGED=NO
PRS_MERGED=NO
FORCE_PUSH=NO
```

结论：本地服务和自动化证据已达到“可启动、可访问、可继续做真实浏览器验收”的状态；尚不能把当前结果报告为完整 Windows GUI 验收、真实设备互操作验收或正式发布通过。
