# R6 执行日志

## 2026-11-07

- 恢复并核对 R4/R3 提交图、主工作树、45 个 worktree 和 dirty worktree 边界。
- 只读审计原版 NetConsole：设备、AC/AP、配置、任务、终端/跳板、诊断、拓扑、文件和系统治理入口。
- 只读审计 SmartHub：clean-room Shell、密集表格、状态标签、详情抽屉、任务/终端模式和 UI gap 优先级。
- 工作台接入真实 `getWirelessAcDomains` / `getWirelessApResources` 汇总，使用 `Promise.allSettled` 隔离无线 API 故障。
- 增加 Dashboard UI 测试：真实 AC/AP 数量、失败任务数量和无线接口失败状态。
- 完成本地前端全量测试、typecheck 和 Vite/静态资源构建；构建产物随工作台改动提交。
- 产品闭环本地提交：`87d19cd`；文档检查点另行提交，以 Git HEAD 为准。
- R6 总状态仍为 PARTIAL；未执行模拟器写操作、远端 push/merge 或 release。

## 2026-11-07 R6.1

- 新增智能配置 dry-run：H3C 接口描述与 Access VLAN 模板、变量校验、命令预览和 SUPERADMIN/Site scope；明确 `DRY_RUN_ONLY`、`deviceWriteAllowed=false`、`writeAttempted=false`。
- 新增拓扑与资产工作台：真实设备库存和 LLDP claims API；保留加载、空、错误、部分 profile 失败、scope/revision/来源/捕获时间状态，不推断邻居或在线状态。
- 配置备份页新增恢复预览、dry-run 和写入阻断状态；现有服务 writer 仍 fail-closed，409 不被渲染为恢复成功。
- R6.1 验证：Web 20 文件/211 tests PASS；SmartConfig C# 5/5 PASS；完整 C# harness EXIT 0；Release Service build 0 warnings/errors；Vite 2095 modules PASS。
- 白名单模拟器四个端口 TCP 可达；本轮没有可安全注入的认证凭据，因此没有执行 SSH/Telnet 登录、配置读取、HA 切换或设备写入。
- 新增 `tests/r6_1_browser_e2e.mjs`：使用脱敏 API fixture 在 1920x1080、2560x1440 完成 SmartConfig、Topology/Asset、ConfigRestore dry-run 与模拟 409 fail-closed 浏览器验收；page error 检查通过，截图保存于工作区 `build/r6-1-browser-e2e/`。
- R6.1 产品与检查点本地提交：`fdf9b20`；远端 push、merge、release 仍禁止。
