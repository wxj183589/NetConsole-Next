# Worktree 回收记录（2026-11-15）

本记录对应 Workspace 整理任务。回收前已完成布局检查、Git 状态检查、主线可达性检查和报告/主仓库文档引用检查。

## 回收原则

- 只回收 clean Worktree。
- 目标 HEAD 已被最新 `github/main` 到达，且没有本地领先于其上游的 Commit。
- 不删除 Branch、Commit、远端引用或主仓库文件。
- 不使用 `--force`、`git clean`、`reset`、`checkout` 或 `stash`。
- 使用 `git worktree remove` 让 Git 同步清理 Worktree 目录和 Worktree 管理元数据。

## 已回收目标

| Worktree | Branch | HEAD | 上游 | 扫描到的内容 |
| --- | --- | --- | --- | --- |
| `worktrees/codex-A-phase9-h3c-zte-parity-20261029` | `codex-A/phase9-h3c-zte-parity-20261029` | `35631e6` | `github/codex-A/phase9-h3c-zte-parity-20261029` | 1,073 文件，约 186.74 MB，6 个 ignored 构建目录 |
| `worktrees/phase2-p2-remediation-20261027` | `codex-A/phase2-p2-remediation-20261027` | `e14da06` | `github/codex-A/next-device-management-h3c-capability-runtime-20261027` | 21,723 文件，约 431.45 MB，7 个 ignored 构建目录 |
| `worktrees/phase2-p2-remediation-export-20261027` | `codex-A/phase2-p2-remediation-export-20261027` | `181940b` | `github/codex-A/next-device-management-export-runtime-20261027` | 980 文件，约 174.24 MB，7 个 ignored 构建目录 |
| `worktrees/phase2-p2-remediation-final-20261027` | `codex-A/phase2-p2-remediation-final-20261027` | `bddb8f8` | `github/codex-A/next-device-management-phase2-final-gate-20261027` | 22,827 文件，约 597.13 MB，13 个 ignored 构建目录 |
| `worktrees/phase2-p2-remediation-rail-20261027` | `codex-A/phase2-p2-remediation-rail-20261027` | `271269b` | `github/codex-A/next-device-management-rail-reference-runtime-20261027` | 996 文件，约 177.19 MB，7 个 ignored 构建目录 |

上述 Branch、Commit 和远端引用保留；回收的是 Worktree 目录及其本地 ignored 构建缓存，不是代码历史。

## 明确保留

- 所有 dirty Worktree：不回收。
- detached Worktree：不回收。
- 没有上游、存在未推送 Commit、未确认合并状态或用途不明的 clean Worktree：不回收。
- 主仓库中的 57 个未跟踪前端资产：不回收、不自动提交。
- `build`、`test-data`、`reports`、`diagnostic`、PoC、历史研究目录和嵌套副本：本次不做批量清理。

## 复核

回收后必须重新运行：

```powershell
python -m scripts.check_workspace_layout
git worktree list
git status --short --branch
```
