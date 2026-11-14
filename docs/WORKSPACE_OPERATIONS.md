# NetConsole-Next Workspace 目录与生命周期规则

本文件是 `D:\study\NetConsole-Next-Workspace` 的目录治理规则，配合
`AGENTS.md`、`docs/WORKSPACE_LAYOUT.md` 和 `tools/workspace_paths.py` 使用。

## 1. 边界与唯一入口

- Workspace 根目录固定为 `D:\study\NetConsole-Next-Workspace`。
- 主仓库固定为 `D:\study\NetConsole-Next-Workspace\NetConsole-Next`。
- Workspace 根不是 Git 工作树。禁止在根目录执行 `git add .`、`git commit`、`git push`、`git clean` 或 `git init`。
- `D:\study` 是项目集合根，不是 Next 的工作目录；不得把 Next 的文件写入 `D:\study` 根或旧版 NetConsole Workspace。
- 代码和测试脚本必须通过 `tools/workspace_paths.py` / `tools/workspace_paths.mjs` 解析受管目录，不得重新硬编码旧散落路径。
- Workspace 根、受管目录和 Worktree 不得通过 Junction、Symbolic Link 或其他 Reparse Point 跨边界访问。

## 2. 顶层目录职责

只允许向以下正式目录写入新的 Next 产物：

| 目录 | 用途 | 生命周期 |
| --- | --- | --- |
| `NetConsole-Next` | 主仓库 | 受 Git 管理；不放运行数据和大型构建输出 |
| `worktrees` | Git 原生 Worktree | 只能用 `git worktree` 管理 |
| `test-data` | 最小 fixture、隔离测试数据和明确保留的测试证据 | 按任务、来源 Commit、保留期限管理 |
| `build` | 构建输出、candidate、截图和浏览器证据 | 可重建但必须先确认未被报告引用 |
| `diagnostic` | 诊断输出和维护报告 | 需要来源、时间和任务归属 |
| `release` | 正式或明确保留的发布候选制品 | 不作为普通临时目录 |
| `poc` | 独立研究仓库 | 与主仓库 Git、产物和测试数据隔离 |
| `reports` | 审计、验收、整理和交接报告 | 历史报告默认保留 |

`release` 可以为空，但不得因此把发布制品写到 Workspace 根或 `build` 外的临时位置。

以下是历史遗留例外，只能保留和审计，禁止继续作为新输出目标：

- `.vs`：IDE 本地状态；不纳入业务产物和提交。
- `architecture-replanning`：历史架构研究交付物；保留原路径作为证据。
- `temp`：历史临时运行输出；新任务不得继续向其中写入。
- `NetConsole-Next-Workspace\NetConsole-Next`：历史嵌套副本；未经单独审计不得移动或删除。

禁止为了“整齐”把历史报告、研究成果、数据库、真实数据或其他 Agent 的证据迁移到新目录。

## 3. 命名和产物记录

- 新建目录使用 `<task>-<purpose>-<YYYYMMDD>` 或 `<task>-<purpose>-<YYYYMMDD-HHMMSS>`；禁止使用无法识别来源的随机目录名。
- candidate、报告、截图和诊断输出至少要能追溯到任务、Branch、Commit、时间和状态。
- 任务专属临时目录必须记录 owner、创建时间、来源 Commit、用途和回收条件；没有这些信息的目录默认保留并标记为待审计。
- 测试只创建最小 fixture、单个数据库 candidate 或单个文件副本。禁止复制完整 Site、完整数据库或整个真实数据根。
- 不得把真实设备数据、SQLite 数据库、Raw 数据、凭据或生产数据写入构建和普通临时目录。

## 4. Worktree 规则

- 只能使用 `git worktree add`、`git worktree move`、`git worktree repair` 和 `git worktree remove` 管理 Worktree。
- 禁止用文件复制、普通移动或递归删除代替 Worktree 操作。
- dirty、detached、未推送、未合并、用途不明或被报告/任务引用的 Worktree 不得回收。
- 回收前必须记录路径、Branch、HEAD、工作树状态、远端关系和未合并 Commit，并确认没有其他 Agent 或进程使用。
- 主仓库和其他 Worktree 的 dirty 修改属于原所有者；禁止 reset、checkout、stash、clean 或覆盖来制造“干净状态”。
- `worktrees` 中出现未被 `git worktree list` 登记的目录时，先停止批量清理；只有确认目录为空且不含 Git 元数据时，才可逐项回收。

## 5. 回收门禁

回收必须按以下顺序进行：

1. 确认精确绝对路径，检查 Reparse Point、Git 归属、文件数量和最后写入时间。
2. 检查报告、脚本、任务记录和其他 Worktree 是否引用该路径。
3. 确认不含 dirty 修改、未推送 Commit、数据库、真实数据、历史证据或用户资料。
4. 确认可以重新生成，且回收不会影响当前运行、验收或发布。
5. 优先回收空的、未登记的、无引用的目录；其他内容先形成清单并等待明确授权。
6. 只对清单中的精确目标执行操作，完成后立即复核目录、Git Worktree 注册表和相关报告。

以下命令永远不能作为 Workspace 整理手段：

```powershell
git clean -fdx
git reset --hard
git checkout -- .
git stash
git push --force
```

## 6. 每次任务开始和结束检查

从主仓库根目录执行：

```powershell
python -m scripts.check_workspace_layout
git rev-parse --show-toplevel
git status --short --branch
git branch --show-current
git log -1 --oneline
git worktree list
```

布局检查必须真实反映当前状态。发现 dirty Worktree、未登记目录、缺失目录、越界路径或 Reparse Point 时，报告 `BLOCKED` 或 `PARTIAL`，不得伪造通过。

## 7. Git 提交边界

- 只在已确认的 `NetConsole-Next` 主仓库或明确的 PoC 子仓库执行 Git 操作。
- 只暂存本任务文件，不使用 `git add .`。
- 构建资产是否应提交必须依据项目发布规则和当前任务确认；不能因为文件位于 `wwwroot` 就自动提交或删除。
- Workspace 根目录的规则修改应优先落在主仓库 `docs/` 和 `AGENTS.md`，不能提交到 `D:\study\.git`。
- Push 只能使用已确认的远端和当前 Branch，禁止 Force Push；Push 失败时保留本地 Commit 并如实报告。
