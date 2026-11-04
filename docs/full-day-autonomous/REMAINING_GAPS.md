# 剩余缺口与阻断

## External

- 四台模拟器缺少可用凭据；自动登录授权已存在，但不得猜测、穷举、记录密码。TCP/协议可达不等于认证或 HA 已验证。
- HTTP/Service-backed Gate 受已有单实例 Mutex 影响；不终止用户服务、不绕过 Mutex。

## Contract / implementation

- 单命令 Raw 已可发布；多命令 collection manifest、命令级状态和批量子任务仍需集成。
- 正式 AP identity/replacement、Domain/group 生命周期和多目标 Report 需要独立持久合同评审。
- Diagnostics、配置备份、拓扑变化历史和完整模块工作台仍待推进。

## Verification boundary

- 离线 fixture、Fake transport、隔离 SQLite 证据不得写成模拟器实时验收。
- 1920×1080 和 2560×1440 是本阶段 UI 视口；125% 不作为本阶段阻断。
