# Linux SSH 服务器与交互终端

Linux 服务器使用独立的 `linux_servers` 目录与 SSH.NET 交互通道。网络设备 CLI 继续统一使用 CPython + Netmiko；Linux 服务器不使用 Site SSH Jump Host，也不进入网络设备 inventory、H3C Capability 或 FIT-AP 关系。

启用现有 Terminal 模块后，`/api/system/linux/servers` 提供服务器列表、新增、编辑、删除；`/{id}/test` 使用保存的密码进行 SSH 认证，`/{id}/sessions` 创建 `LINUX_INTERACTIVE` 会话。读取、测试、终端和历史均应用 Site scope；新增、编辑和删除要求 SUPERADMIN。服务器名称、地址、端口、用户名、分组、Site 和启用状态保存在 SQLite。分组列表由现存服务器的非空分组名称生成。

## 凭据和本机目录

保存密码后，测试和终端自动按服务器 ID 读取；省略或留空保留原密码，`clearPassword=true` 明确清除。API 只返回 `credentialConfigured`。普通设备和 Linux 密码均为本机 SQLite 字段，不使用 DPAPI，不宣称数据库加密。Host Key 和密码分别保存。

本批提供密码认证。原版源码的 SSH/中转与 external terminal 入口未发现可继承的 Linux 私钥管理流程；不把依赖库支持私钥等同为产品已有私钥功能。

Catalog 启动在确认 Next 数据根标记后、创建 token 和打开数据库前限制数据根访问权限。Windows 使用不继承父目录的 DACL，仅授予当前服务运行账号、SYSTEM、Administrators 完全控制，并将权限继承至子目录和文件；其他系统使用目录模式 `0700`。权限设置失败会停止初始化。新建的隔离 Windows 测试目录已验证既有和新建文件的继承 ACL；Linux 分支仍需目标 Linux 环境验证。已有文件显式配置的特殊权限不做递归清理。

## 指纹、生命周期和历史

首次与变化指纹在 SSH 认证成功后自动保存到 Linux 专用记录，并追加 `linux_host_key_events`。认证失败不保存；连接期间配置改变或其他连接更新为不同指纹时，旧连接不能覆盖新状态。同一配置的并行连接若观察到同一指纹，允许共同成功且仅保留一条变化事件。

Linux 键盘通过现有终端 WebSocket 的 `input.data` 发送，支持控制字符、ANSI 输出和 PTY resize。网络设备继续使用受现有命令策略约束的 `input.text`。Linux 不持久化原始键盘输入，避免记录 sudo 或应用密码；输出录像仍可能包含远端程序回显。会话、事件、输出录像、导出和 Site 授权复用已有终端能力。

总终端并发为 8，Linux 连接测试并发为 4。Linux 默认 900 秒无输入和输出时断开，可通过 `NETCONSOLE_NEXT_TERMINAL_IDLE_TIMEOUT_SECONDS` 设置为 1–86400 秒。编辑或删除服务器会关闭对应旧会话，创建和修改共用现有创建锁。删除保留历史会话、录像引用和指纹事件；历史按会话建立时的 Site ID 授权，不依赖仍存在的服务器记录。

连接测试重读服务器后再次检查 Site scope；服务器在请求过滤后迁移到其他 Site 时返回 404。WebSocket 除收发检查外，每秒复核一次权限，静默会话撤权后也会关闭并释放 SSH，记录 `SITE_SCOPE_REVOKED`。

Schema 23 扩展 `terminal_sessions.mode` 的 CHECK 约束。SQLite 表重建在事务内保留原表全部字段、索引、事件、录像分段和导出关联，提交前检查外键；失败回滚并恢复外键约束。启动及再次启动的合成旧库回归验证历史字段保持一致。

## 验证边界

定向测试入口为 `dotnet run --project tests/NetConsole.Next.Service.Tests -c Release -- --linux-server-tests`。它涵盖凭据保留和清除、安全视图、Site scope、Host Key 并发、旧终端迁移、回滚、重复迁移、Catalog 重启及本机目录权限。HTTP/WebSocket 模拟器回归入口为 `python tests/linux_ssh_e2e.py`，浏览器入口为 `node tests/linux_browser_e2e.mjs`；结果以本轮实际运行证据为准。

模拟器通过不表示真实 Linux 服务器、SSH 堡垒机或网络设备兼容性。缺少实机证据的项目保持 `REAL_DEVICE_PENDING`。
