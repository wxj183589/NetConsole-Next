# NetConsole-Next Workspace Layout

NetConsole-Next 的开发工作区唯一根目录为：

```text
D:\study\NetConsole-Next-Workspace
```

目录职责如下：

```text
NetConsole-Next-Workspace/
├── NetConsole-Next/       # 唯一主仓库
├── worktrees/             # 仅 Git 原生 Worktree
├── test-data/             # 隔离测试数据和明确保留的证据
├── build/                 # 构建中间文件、截图和浏览器证据
├── diagnostic/            # 诊断输出和维护报告
├── release/               # 发布候选制品
├── poc/                   # 独立研究仓库，不与主仓库混合
│   └── NetConsole-Next-PoC/
└── reports/               # 整理、验收和交接报告
```

Windows 默认通过 `NETCONSOLE_NEXT_WORKSPACE_ROOT` 覆盖根目录；未设置时使用上面的固定根。覆盖值不能是旧的散落目录、`D:\study` 本身或其任意普通直系子目录，也不能经过符号链接或 Junction。代码和测试应通过 `tools/workspace_paths.py` 的 `workspace_root()` / `workspace_path()` 解析路径，禁止把输出路径写回 `D:\study\build`、`D:\study\diagnostic`、`D:\study\test-data` 或 `D:\study\worktrees`。

Git Worktree 必须使用 `git worktree add`、`git worktree move`、`git worktree repair` 和 `git worktree remove` 管理。dirty Worktree 不得回收；未提交修改、未推送提交、真实数据、SQLite 数据库和研究成果不得按缓存处理。

NetConsole-Next 使用独立数据 Authority，不得读取或修改旧 NetConsole 的 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，也不改变旧 NetConsole 的 `D:\study\NetConsole-Workspace` 存储策略。历史报告保留原路径作为审计证据，不表示旧目录仍可作为新输出目标。
