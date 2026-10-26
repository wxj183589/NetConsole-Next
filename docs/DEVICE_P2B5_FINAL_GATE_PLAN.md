# P2B5 Final Regression and Handoff Gate Plan

日期：2026-10-27  
Branch：`codex-A/next-device-management-final-gates-20261027`  
基线：Rail base-data boundary `7b7fecb`  
状态：Draft；最终门槛清单，未声明 Phase 2 已合并或正式验收

## Gate 1：依赖与审查

- PR23–PR34 的 base/head、祖先关系、diff 范围、review thread、Draft 状态和 CI 结果必须逐项记录。
- 正式 reviewer approval、P0/P1 结论和 owner 对迁移/写入/站点范围的授权缺一不可；本任务不自动 Ready、Approve 或 Merge。
- main、Production、Dev Authority 和 Legacy HistoryStore 不得因本阶段门禁发生变化。

## Gate 2：服务与数据安全

- Release build、完整 C# runner、P2B3/P2B4 HTTP E2E、schema v13/v14 isolated migration、重启恢复、幂等、revision conflict、atomic rollback、audit 和权限拒绝全部通过。
- 启动路径不能隐式迁移 v13/v14；所有 schema candidate 必须显式 isolated authority、路径边界、connection identity 和 feature gate。
- ADMIN 缺少可信 site claim 时保持 denied；跨 site/domain/address/VLAN 查询和写入必须返回结构化拒绝或 conflict。
- 不读取或复制 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，不连接真实设备，不把真实现场数据作为自动化成功证据。

## Gate 3：Vue/浏览器

- 真实 .NET service + Chrome/Playwright 通过 1920x1080、1440x900、1366x768，覆盖 light/dark、刷新恢复、地址/domain/VLAN/diff/review/history、CSV preview/dry-run/confirm/cancel 和 operation history。
- 证据必须包含真实截图、JSON 记录、console errors、page errors、failed requests 和 unexpected HTTP 计数；预期 401/403 不计为异常，但要单独记录。
- feature gate 或 scope 不满足时页面显示受控不可用状态，不显示 fake rows、fake API payload 或可造成误解的编辑按钮。
- 大数据量分页、键盘访问、表格密度、暗色对比度和窗口缩放需在正式 UI review 中复核；本地隔离 E2E 不能替代跨机/人工验收。

## Gate 4：业务边界

- 导出只能来自固定 revision/snapshot，并保留 provenance/hash/manifest；不接入 WPS 云/API、KDocs 或隐式同步。
- 轨交基础数据只允许来自可信 source revision 和显式 identity；禁止站点硬编码、模糊匹配和未审计的自动补全。
- H3C V5/V7 仍 mock-only；真实 SSH/Telnet/COM、真实凭据、现场 VLAN 和设备写入均保持 `REAL_DEVICE_PENDING`。

## 当前状态与后续

本计划只固化最终验收矩阵，不改变运行时。当前状态应保持 `P2B5_DRAFT_CHAIN_VERIFIED_APPROVAL_PENDING`：P2B5 UI、Site Scope、H3C mock、Export boundary、Rail base-data boundary 已拆分为 Draft 链；任何合并、正式部署、真实站点 claim、生产/开发 migration、真实设备和现场数据验收都必须由后续授权任务单独完成。
