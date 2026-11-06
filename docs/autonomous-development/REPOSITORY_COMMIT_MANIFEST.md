# Repository Commit Manifest

日期：2026-11-07（Asia/Shanghai）

## 当前产品提交链

| Commit | 类型 | 内容 | 是否进入当前分支 |
| --- | --- | --- | --- |
| `6809701` | fix | 同设备互斥等待取消终态 | 是 |
| `eef3c4a` | checkpoint | 续跑 dirty 修改和恢复报告 | 是 |
| `cf0e172` | feat | STARTUP_BODY 两阶段只读采集 | 是 |
| `2a58ae9` | feat | AP identity review/revision 只读合同 | 是 |
| `86d3190` | fix | 禁止 STARTUP_BODY 进入定时计划 | 是 |
| `3c0e15a` | test | 启动正文两阶段后端回归 | 是 |
| `41bd2e2` | feat | 启动正文 API/UI 接入 | 是 |
| `ad3a574` | docs | 续跑检查点和交接证据 | 是 |

## 有效但未合并的历史工作流

`codex-A/full-day-concurrency`、`codex-A/netconsole-next-identity-contract` 等分支的已验证提交已通过 cherry-pick 或等价提交进入当前分支；对应 worktree 保留。其他旧设备管理、SQLite、review、UI 和 phase 分支未因存在而视为已合并，是否合并需要逐分支审计。

## R4 新工作流

当前新建的三个分支均从 `ad3a574` 创建，尚无 R4 产品提交。每个代理必须在独立 worktree 提交代码和测试后，主协调工作流再执行 diff、构建、定向测试和本地合并。

## R4 已集成提交

| Commit | 类型 | 内容 | 证据 |
| --- | --- | --- | --- |
| `fd48fb5` | feat | 按 run ID 查询任务，Site scope 和 URL 编码保护 | 并发 `6/6`、前端 client 回归 |
| `80e6816` | feat | 配置恢复 preview、dry-run、confirmation token、audit 和 disabled writer | `CONFIG_RESTORE_TESTS=11/11` |
| `d253163` | feat | AP identity lifecycle authority 与 API | lifecycle `5/5`、identity contract `3/3` |
| `2a4d65f` | fix | SUPERSEDE replacement 当前 Site 资源范围校验 | Release build 0 warnings/0 errors |

当前产品 HEAD 为 `2a4d65f`。以上均为本地提交；本轮未 Push、远程 Merge 或 Release。
