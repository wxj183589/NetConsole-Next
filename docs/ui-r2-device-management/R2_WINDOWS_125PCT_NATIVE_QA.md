# R2 Windows 125% Native GUI QA

## 结论

`WINDOWS_NATIVE_125PCT=PENDING`。本机没有提供可据此批准的有效原生 Windows 125% 显示器环境，本轮没有修改全局缩放、注销、重启或影响其他程序。

## 实测环境（2026-11-04）

| 项目 | 证据 | 结论 |
| --- | --- | --- |
| 旧式 Windows 缩放注册表 | `HKCU\Control Panel\Desktop\LogPixels=120`、`Win8DpiScaling=1` | 配置残留/系统级 125% 信号 |
| 当前有效显示器 | Win32 `GetDpiForMonitor=96`、Scale Factor=`100`；有效桌面 `2560×1440` | 当前显示器有效缩放为 100%，不是 125% |
| 原生 Chrome 窗口 | Chrome `156.0.8078.74`；Win32 `GetDpiForWindow=96` | 窗口 DPI=100% |
| Chrome 页面 | headed probe：`devicePixelRatio=1`、`visualViewport.scale=1`、CSS viewport=`1249×1277` | 未发现浏览器页面缩放或 DPR 125% |
| Electron | NetConsole-Next 当前仓库没有 Electron 工程、依赖或 `BrowserWindow` 入口 | Electron 窗口尺寸不适用 |
| 截图像素 | 既有 R2 visual fixture 明确使用 CSS viewport 与 `deviceScaleFactor=1` | 只能作为模拟视口证据 |

`LogPixels=120` 不能覆盖当前显示器 API 返回的有效 96 DPI；因此没有把注册表值、CSS viewport、DPR 或截图像素混为“原生 125%”。

## 已有 GUI 证据

- `1920×1080`：列表、作用域、筛选、弹窗和详情截图 PASS。
- `2560×1440`：辅助视口 PASS。
- `1536×864`：响应式/125% CSS 视口模拟 PASS，但 `deviceScaleFactor=1`，不是原生 125%。
- 创建、详情和服务型 CRUD 浏览器流程均有脱敏截图；截图不包含真实密码或真实设备数据。

## Gate 处理

本轮不修改 Windows 全局缩放，也不强制注销/重启。若要关闭原生 125% Gate，需要用户在实际有效显示器缩放为 125% 的环境中重新执行列表、创建、编辑、详情、空态、长字段和弹窗截图；在此之前保持 `PENDING`，不得用模拟结果升级为 PASS。
