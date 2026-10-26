# P2B5 H3C V5/V7 Capability Mock Boundary

日期：2026-10-27  
Branch：`codex-A/next-device-management-h3c-capability-20261027`  
基线：Site Scope boundary commit `c719a2f`  
状态：Draft，mock-only 规划；未连接真实设备

## 本阶段允许的内容

- 只定义 capability contract、版本识别结果和 mock fixture 的字段边界。
- mock 输入可以来自 `tests/NetConsole.Next.Service.Tests/fixtures/h3c` 或内存 fixture；必须标注 `MOCK`/`SIMULATED` source。
- V5/V7 只作为 capability labels 和 parser mapping 讨论，不把版本识别当作设备授权、站点授权或可写能力。
- 结果必须保留 `vendor`、`version`、`release`、`recognized`、`source`、`observedAt` 和 `confidence`，未知格式保持 `UNRECOGNIZED`。

## 明确禁止

- 不添加真实设备 IP、账号、密码、Host Key、VLAN 或现场站点数据。
- 不在 P2B5 UI 或测试中连接 SSH/Telnet/COM 真实 transport。
- 不把 Python resident process、脚本 daemon、全局 adapter framework 或自动重试引入服务。
- 不把 V5/V7 capability mock 直接变成配置写入、采集调度或命令执行权限。
- 不宣称 V5/V7 兼容已通过真实设备验收；真实设备与跨版本 acceptance 仍为 `REAL_DEVICE_PENDING`。

## 与现有边界的关系

现有 .NET H3C SSH 代码已经有 allowlisted read-only command、Host Key pin、DPAPI credential boundary、Raw/Catalog persistence 和 partial-output 规则。本 Draft 不改动这些运行时路径；P2B5 network planning 仍由 v14 isolated gate 和 Site Scope boundary 控制。

未来如果实现 capability service，必须先提供独立的 mock contract tests，再由小 Draft PR 逐步接入 .NET service；每一步都保留真实 transport disabled-by-default、命令 allowlist、source provenance、audit 和 failure-category redaction。

## 验收声明

本阶段的合格条件是：文档契约可审查、mock fixture 可重建、没有真实设备副作用。任何真实 V5/V7 设备、现场凭据、跨机器 GUI 或生产数据验收都不在本 PR 的成功范围内。
