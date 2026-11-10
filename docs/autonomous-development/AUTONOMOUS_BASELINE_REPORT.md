# NetConsole-Next 自主开发基线报告

日期：2026-11-11  
执行 Agent：codex-A  
仓库：`D:\study\NetConsole-Next-Workspace\NetConsole-Next`  
本阶段 worktree：`D:\study\NetConsole-Next-Workspace\worktrees\codex-A-device-credential-dpapi-20261111`

## 基线结论

- GitHub 远端 `main` 的实际提交为 `230b830e623020ad15664fe701c2d9c3c9d1330f`，对应已合并的 PR #48。
- 主工作树当前位于 `codex-A/netconsole-next-r3-dual-ac`，HEAD 为 `84e53533e5197aa4075c53f98cf5fb0775189a05`。该工作树跟踪的 R3 集成线与远端 `main` 已明显分叉，存在一批未跟踪的 Web 构建 assets；本任务未修改、清理或覆盖它们。
- 本阶段从 `github/main` 建立独立分支 `codex-A/device-credential-dpapi-20261111`，只处理 PR #48 审查确认的设备凭据异常边界和 SSH 主机密钥确认状态问题。
- 当前仓库没有独立的 `NetConsole-Next-DevStatus` 目录；本分支使用 `docs/autonomous-development/` 保存可追踪交接记录，不伪造另一个状态仓库。
- `python -m scripts.check_workspace_layout` 在当前仓库不可用，因为没有该模块；仓库实际提供 `tools/workspace_paths.py`。这已记录为开发差距，未因此创建额外目录或改变工作区布局。

## 已核对的远端上下文

- GitHub 仓库默认分支为 `main`；本次远端 PR 清单未发现仍开放的 PR。PR #48 已合并。
- PR #48 的审查线程有两个未解决问题：新增设备 DPAPI 不可用时会由路由冒泡为 HTTP 500；修改设备管理地址后 UI 仍会沿用旧的 SSH 主机指纹。本阶段已在独立分支修复，未修改已合并 PR 的历史线程。
- 远端 `main` 已有设备库存、Site/Group、幂等创建、终端 profile 和 DPAPI 补偿的实现；无线 AC/FIT-AP 仍不能据此宣称已完成真实业务闭环。

## 数据边界

本阶段未读取、复制、修改 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，没有生产数据库、完整 Site 或真实设备凭据进入测试。测试只使用临时 SQLite/凭据目录；测试结束后临时目录已回收。

