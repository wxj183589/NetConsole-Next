# Continuation Test Results

日期：2026-11-07（Asia/Shanghai）

## 本轮新增或重跑

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Service/Tests Release build | PASS | `dotnet build ... -c Release --no-restore`, 0 warnings/0 errors after `86d3190` |
| Startup body and restore boundary | PASS | `CONFIG_BACKUP_TESTS=11/11 PASS`; two-stage `display startup` → `more` order, snapshot/download body, invalid path, traversal, injection and NULL pointer fail closed; restore returns explicit unsupported boundary |
| Backup schedule regression | PASS | `CONFIG_BACKUP_SCHEDULE_TESTS=15/15 PASS`; `STARTUP_BODY` cannot enter durable schedules |
| Wireless identity contract | PASS | `WIRELESS_IDENTITY_CONTRACT_TESTS=3/3 PASS` |
| Wireless regression | PASS | Group contract, cache `4/4`, replacement `3/3`, projection and runtime `13/13` PASS; identity contract included |
| Same-device cancellation | PASS | `CONCURRENCY_P1_TESTS=6/6 PASS` |
| Frontend continuation baseline | PASS | `pnpm test` repository suite: 17 files / 199 tests; `pnpm typecheck` and Vite build (2089 modules) PASS |
| Existing full Service harness | PASS | continuation run before final batches: vendor replay, Raw/Facts, migrations, wireless/runtime, diagnostics `9/9`, Traceroute `15/15`, schedules `15/15`, backups and concurrency `5/5`; process exited 0 |

## 未执行或不能宣称

- 没有重新构建或运行当前 SHA 的离线发布候选；历史 `ef57213` 候选证据不覆盖本轮提交。
- 没有执行远端 CI、Windows 原生 125% GUI、目标机安装/SCM/升级/卸载或跨机器 TLS。
- 没有使用真实模拟器凭据；TCP 可达不等于认证、命令输出或 HA 验收。
- `scripts.check_workspace_layout` 在当前仓库不存在，已记录为工具缺失；未因此创建额外目录。
