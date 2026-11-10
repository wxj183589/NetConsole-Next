# NetConsole-Next Worktree Inventory

盘点日期：2026-11-11  
盘点命令：`git worktree list --porcelain`  
记录到的 Git worktree 数：67

## 本任务相关 worktree

| 路径 | 分支 | HEAD | 状态 | 处理 |
|---|---|---|---|---|
| `D:\study\NetConsole-Next-Workspace\NetConsole-Next` | `codex-A/netconsole-next-r3-dual-ac` | `84e5353` | tracked 文件未改；存在未跟踪 `wwwroot/app/assets/*` 构建产物 | 保留，不清理、不覆盖 |
| `D:\study\NetConsole-Next-Workspace\worktrees\codex-A-device-credential-dpapi-20261111` | `codex-A/device-credential-dpapi-20261111` | `8828d0a` | clean，基于 `github/main` | 本阶段修改与验证 worktree |
| `D:\study\NetConsole-Next-Workspace\NetConsole-Next-Workspace\NetConsole-Next` | 旧/嵌套 workspace 检查对象 | 未在本阶段使用 | 用途与状态需后续单独审计 | 保留，不删除 |

## 其它并行 worktree

其余 64 个 worktree 属于历史 R3/R4/R7、设备管理、无线、配置恢复和验收任务线。它们不属于本阶段修改范围；没有执行 `git worktree remove`、分支删除、清理或 stash。需要完整实时清单时应在仓库根目录重新运行：

```powershell
git worktree list --porcelain
```

## 工作区安全说明

- 新 worktree 通过 `git worktree add -b codex-A/device-credential-dpapi-20261111 ... github/main` 创建，未复制真实数据。
- 本阶段生成的 `web/node_modules` 和隔离测试临时目录已在验证后回收。
- 主工作树的未跟踪 Web assets 被视为既有用户/并行任务状态，未纳入本分支，也未删除。

