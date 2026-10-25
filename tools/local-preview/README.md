# NetConsole Next Windows 本地预览

这些脚本只启动当前工作树构建出的 NetConsole Next Service，不安装 Windows Service、不打开 LAN、不连接 H3C/MR、不使用 D:\NetConsoleData 或 D:\NetConsoleData-dev。

## 启动

在仓库根目录执行：

~~~powershell
.\tools\local-preview\Start-NetConsoleNext-Preview.ps1
~~~

默认配置：

~~~text
URL:       http://127.0.0.1:18771
数据根:    D:\NetConsoleNext-PreviewData
Syslog:    关闭
H3C:       关闭
Terminal:  关闭
LAN:       关闭
~~~

如果端口被其他程序占用，脚本不会停止它；改用独立端口：

~~~powershell
.\tools\local-preview\Start-NetConsoleNext-Preview.ps1 -Port 18772
~~~

NoBrowser 可用于只启动服务而不打开默认浏览器，SkipBuild 仅在当前 Release Service 和 wwwroot 已确认最新时使用。

本地预览种子账号仅用于隔离预览：

~~~text
admin / admin
superadmin / superadmin
~~~

登录成功不等于真实设备、真实 H3C 或正式数据验收通过。预览首次启动后请立即修改密码；不要在正式环境复用这些口令。

## 检查与停止

~~~powershell
.\tools\local-preview\Test-NetConsoleNext-Preview.ps1
.\tools\local-preview\Stop-NetConsoleNext-Preview.ps1
~~~

Stop 只会停止自己写入 preview-state.json 且启动时间匹配的 .NET 进程；它不会结束其他 .NET 进程，也不会删除数据库、Raw、日志或测试数据。重复 Stop 是安全的。

日志位置：

~~~text
D:\NetConsoleNext-PreviewData\preview-logs\service-*.stdout.log
D:\NetConsoleNext-PreviewData\preview-logs\service-*.stderr.log
D:\NetConsoleNext-PreviewData\logs\
~~~

## 当前真实 UI 验收范围

| 页面 | 当前状态 |
| --- | --- |
| 登录页 | 已实现的 Vue 页面；脚本只验证页面可访问，不宣称已登录 |
| 运行总览 | 已实现真实 health/task 快照、状态卡片和采集曲线占位 |
| 设备管理 | 已实现真实 Catalog 列表和登记入口；不生成虚假设备 |
| 任务中心 | 已实现真实任务状态和入口；未开启 H3C 时不会出现真实采集结果 |
| 内置终端 | 已实现 Vue/xterm 页面与多标签结构；本预览默认禁用真实终端 |
| 工具集 | 已实现宿主工具登记/状态结构；不会自动启动外部程序 |
| 文件管理 | 已实现逻辑根、列表、单文件下载和 ZIP 任务；空目录保持为空 |
| 系统设置 | 已实现设置菜单与密码/运行设置结构 |
| 无线业务 | 禁用占位，尚未实现 |
| 轨道交通业务 | 禁用占位，尚未实现 |

禁止把空列表、禁用页面或模拟协议测试写成真实设备、轨旁 AP、MR 或现场业务已完成。

## 失败恢复

- npm ci 失败：检查 Node/npm 与 web/package-lock.json 是否匹配；不要删除整个仓库或数据根。
- 端口冲突：先查看脚本报出的 PID/进程，再使用 -Port 18772 等独立端口。
- 健康检查失败：查看 preview-logs\service-*.stderr.log 和 logs\，脚本不会自动关闭未知进程。
- 数据根安全检查失败：使用新的空目录或保留合法 .netconsole-next-root 标记；不要指向旧 NetConsole 数据。
- 服务退出后：可重复运行 Start；已有预览数据不会被清空或覆盖。

此脚本不能替代 Windows 浏览器人工验收、真实 H3C 授权验收、设备互操作、长周期运行和正式发布 Gate。
