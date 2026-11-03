# R2 GUI Visual QA

## 方法

使用 `tests/r2_device_management_ui_visual.mjs` 启动本地静态构建产物，并用固定、非真实 fixture 走登录、设备列表、创建弹窗和详情弹窗。脚本记录 console/page errors，并由 `view_image` 对截图逐张检查。

## 结果

| 视口 | 证据 | 结果 |
| --- | --- | --- |
| 1920×1080 | `final/devices-1920x1080.png` | PASS（列表、scope、筛选、操作列清晰） |
| 2560×1440 | `final/devices-2560x1440.png` | PASS（2K secondary；页面保持可读的最大内容宽度） |
| 创建弹窗 | `final/device-create-dialog-1920x1080.png` | PASS（Site、认证引用、FIT-AP 边界清晰） |
| 详情弹窗 | `final/device-detail-dialog-1920x1080.png` | PASS（稳定身份、安全、能力与历史分段） |
| Windows 125% | `final/devices-windows-125pct-simulated.png` | SIMULATED PASS（1536×864 CSS 视口，响应式 scope 与列降级） |

脚本报告：`consoleErrors=[]`、`pageErrors=[]`，每个列表视口均读取 3 行 fixture。

## 125% 限制

当前运行环境未改变真实 Windows 系统 Display Scaling；125% 结果是 Playwright CSS viewport simulation，报告中的实际 browser `devicePixelRatio` 仍为 1。因此不能将其升级为真实 Windows 125% GUI acceptance，真实缩放验收保持 pending。

## SmartHub 资产

没有复制 SmartHub 专有资产、字体、bundle、logo 或原始页面实现。

