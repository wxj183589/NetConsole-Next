# Local Repository Inventory

日期：2026-11-07（Asia/Shanghai）

| 类型 | 实际路径 | 分支/HEAD | 状态 | 处理 |
| --- | --- | --- | --- | --- |
| NetConsole-Next 产品 | `D:\study\NetConsole-Next-Workspace\NetConsole-Next` | `codex-A/netconsole-next-r3-dual-ac` / `ad3a574` | clean | 本轮主提交目标 |
| NetConsole-Next autonomous worktree | `D:\study\NetConsole-Next-Workspace\worktrees\next-autonomous-full-feature` | `codex-A/netconsole-next-autonomous-full-feature` / `816e144` | clean | 历史成果保留 |
| NetConsole-Next R4 identity | `D:\study\NetConsole-Next-Workspace\worktrees\r4-identity-lifecycle` | `codex-A/r4-identity-lifecycle` / `ad3a574` | clean at creation | 当前代理工作流 |
| NetConsole-Next R4 restore | `D:\study\NetConsole-Next-Workspace\worktrees\r4-config-restore` | `codex-A/r4-config-restore` / `ad3a574` | clean at creation | 当前代理工作流 |
| NetConsole-Next R4 scheduler | `D:\study\NetConsole-Next-Workspace\worktrees\r4-scheduler-evidence` | `codex-A/r4-scheduler-evidence` / `ad3a574` | clean at creation | 当前代理工作流 |
| Legacy NetConsole product | `D:\study\NetConsole-Workspace\NetConsole` | `main` | read-only reference | 本轮不修改 |
| DevStatus | `D:\study\NetConsole-Workspace\NetConsole-DevStatus` | `main` / `c3aa244` | clean, ahead 21 | 共享交接仓库，需单独记录 |
| fping | `D:\study\fping` | Git root resolves to `D:\study` | out of scope | 不修改、不清理 |
| BiaoGan-Research | `D:\study\BiaoGan-Research` | `main` | out of scope | 不修改 |
| rea | `D:\study\rea` | detached/unnamed | out of scope | 不修改 |

NetConsole-Next 的 remote 配置包含 `github`、`gitea` 和 `origin`；本轮全部禁止 push。未发现独立的 NetConsole-Next 专属 DevStatus 仓库；现有 DevStatus 属于 `NetConsole-Workspace` 共享交接体系。

## Worktree 保护

共有 45 个 NetConsole-Next worktree，4 个 dirty：`full-day-connection`、`phase2-pr-review-and-merge-gate-fix-20261027`、`phase2-pr-review-merge-sim-20261027`、`sqlite-cve-2025-6965-fix-20261027`。它们的 dirty 文件未纳入本轮提交。
