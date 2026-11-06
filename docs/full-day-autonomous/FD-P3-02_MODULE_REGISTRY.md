# FD-P3-02 模块注册与导航错误状态

## 2026-11-06 当前状态补充

本批功能已本地提交 `a3954a5`，文档以所属 commit 为准。模块注册已包含服务端 schema v17 持久化、SUPERADMIN 启停、核心模块保护、API 门禁、重启保留和 Settings 管理 UI；不再只有前端 registry。当前 Service harness、Vue typecheck、8 files/23 tests 及已执行的设置页浏览器场景 PASS。

AC1/AC2/SW 当前 Netmiko 只读连接/命令通过，FIT-AP 经 AC 管理；旧凭据引用不再是当前前置。剩余完整工作台、原生 Windows 125%、跨机器 Web/TLS 与目标机安装仍待完成。

## 以下为 2026-11-05 前端原批次历史记录

下文“只修改前端”与原测试数量仅描述该批次，不覆盖后续模块治理实现。

日期：2026-11-05
Branch：`codex-A/netconsole-next-r3-dual-ac`

## 完成内容

- 新增 `web/src/moduleRegistry.ts`，集中登记模块路径、显示名、图标、组件和 `READY`/`DISABLED` 状态。
- Vue Router 与 `AppShell` 左侧导航均由同一 registry 生成；工作区标签也使用 registry 的显示名，消除重复路径和标签表。
- 禁用模块继续保持显式禁用，不创建模拟业务数据；点击时展示模块自身的禁用原因。
- 新增未知路由页面和异步模块加载失败页面。失败状态保留目标路径和错误详情，并提供返回运行总览操作。
- 补充 registry 单元测试和错误状态样式。

## 验证

- `vue-tsc --noEmit`：PASS
- Vitest：`5 files / 9 tests` PASS
- Vite build：PASS（保留既有大 chunk warning；无 error）
- `python tests/web_ui_contract.py`：`3 checks PASS`
- `git diff --check`：PASS

## 边界

本项只修改前端路由、导航和错误状态；不改变 API、数据库、权限、真实设备或真实数据。四台模拟器的自动登录仍需已有安全凭据引用，不能以 UI 状态替代真实认证验收。
