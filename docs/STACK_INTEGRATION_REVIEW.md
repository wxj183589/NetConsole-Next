# PR Stack Integration Review

审计日期：2026-10-24（Asia/Shanghai）  
仓库：`wxj183589/NetConsole-Next`  
审计分支：`audit/p0-g1-stack-integration`，基于 PR #18 HEAD `69ed4495abc74feb54723c54addb708aecfbec52` 的临时 worktree。

本文件记录的是合并前审计和临时集成修复，不表示任何 PR 已合并。没有修改 `main`、正式 Raw 数据或旧 NetConsole 仓库。

## 1. PR 依赖图和实时状态

实际 GitHub PR 依赖关系如下（按当前 base/head ref 查询）：

```text
main
└─ #1  feat/p0-core-raw-syslog-bootstrap
   └─ #5  feat/g1-stability-recovery-manager
      └─ #6  feat/collector-scheduler-foundation
         └─ #7  feat/g1-service-manager-hardening
            └─ #8  feat/g1-incremental-recovery-persisted-tasks
               └─ #9  feat/g1-5-syslog-throughput-observability
                  └─ #10 feat/g2-h3c-ssh-collection
                     └─ #11 feat/g2-1-h3c-ssh-hardening
                        └─ #12 feat/g2-3-integrated-terminal-console
                           └─ #13 feat/g2-3-terminal-web-ui
                              └─ #14 feat/g2-3-terminal-session-history
                                 └─ #15 codex-A/g23-recording-reliability-20261022
                                    └─ #16 codex-A/g24-device-compat-20261022
                                       └─ #17 codex/web-pr1-vue-ui
                                          └─ #18 codex/web-pr2-host-tools-files
```

PR #2、#3、#4 当前不存在，未按编号推断。

关键实时状态：

| PR | 当前 head | GitHub base ref 当前 head | merge base | base...head | PR #1 修复提交是否在 head | GitHub mergeable | CI |
|---:|---|---|---|---:|---|---|---|
| 1 | `bc0392009249` | `main@9c544d2e4c40` | `9c544d2e4c40` | `0/19` | 是 | MERGEABLE | Ubuntu/Windows PASS |
| 5 | `6a8906a235e9` | `#1@bc0392009249` | `0654f0de97de` | `4/3` | 否 | CONFLICTING | Ubuntu/Windows PASS |
| 6 | `3615325402bc` | `#5@6a8906a235e9` | `6a8906a235e9` | `0/4` | 否 | MERGEABLE | Ubuntu/Windows PASS |
| 7 | `9c574ab50021` | `#6@3615325402bc` | `3615325402bc` | `0/2` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 8 | `1dcbea1f4133` | `#7@9c574ab50021` | `9c574ab50021` | `0/4` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 9 | `a1f8bdd05df3` | `#8@1dcbea1f4133` | `1dcbea1f4133` | `0/6` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 10 | `e41f87333373` | `#9@a1f8bdd05df3` | `a1f8bdd05df3` | `0/3` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 11 | `82f2a0525870` | `#10@e41f87333373` | `e41f87333373` | `0/10` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 12 | `27174402b076` | `#11@82f2a0525870` | `82f2a0525870` | `0/7` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 13 | `cafd9cab3108` | `#12@27174402b076` | `27174402b076` | `0/2` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 14 | `bf311cee3298` | `#13@cafd9cab3108` | `cafd9cab3108` | `0/1` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 15 | `439ee27ce90e` | `#14@bf311cee3298` | `bf311cee3298` | `0/1` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 16 | `293abdf4fbbf` | `#15@439ee27ce90e` | `439ee27ce90e` | `0/1` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS；Codex review failed to complete |
| 17 | `019e75479756` | `#16@293abdf4fbbf` | `293abdf4fbbf` | `0/3` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |
| 18 | `69ed4495abc7` | `#17@019e75479756` | `019e75479756` | `0/7` | 否 | MERGEABLE | Ubuntu/Windows/PowerShell PASS |

PR #5 的 GitHub PR 记录仍以旧 `0654f0de97de` 为合并基点；当前 base ref 已移动到 PR #1 HEAD，因此 GitHub 将 PR #5 标为冲突。PR #6–#18 是 PR #5 的后继提交图，不能由其“绿色 CI”推导出已包含 PR #1 的三项后追加修复。

## 2. PR #1 最终状态

- PR：[#1](https://github.com/wxj183589/NetConsole-Next/pull/1)
- base：`main@9c544d2e4c404015e732fdcfd05a031636ee1db2`
- head：`bc03920092493e36611b634c6119d76203c13fb7`
- 状态：OPEN，MERGEABLE，未合并
- 未解决评审线程：0
- 最新 GitHub Actions：[run 40094048782](https://github.com/wxj183589/NetConsole-Next/actions/runs/40094048782)，Ubuntu/Windows 均成功
- 最新 Codex 复审：`bc03920092`，未发现 major issues

PR #1 的原始评审线程和后续 `byte_count` 线程均已关闭；本审计没有对 PR #1 做新的代码修改。

## 3. RawSegmentWriter 语义审计

PR #18 当前实现已经使用 `MemoryStream`、`Utf8JsonWriter`、LF NDJSON、H3C Raw 和恢复计时，但仍有两项与 PR #1 同类的 P1：

1. `SealAsync` 在 `.open` 移动为 `.ndjson` 后才执行 Catalog 登记，`_sealed` 只在登记成功后设置。登记失败后再次 Seal 会再次读取不存在的 `.open`。
2. Append、Flush、Seal、`AppendH3cCommand` 没有共同的操作门，多个 Seal 或 Append/Seal 并发时会竞争 `_stagedBytes`、Stream、文件移动和状态字段。

临时集成修复保留了现代缓冲、H3C 输出和性能计时，并增加：

- `_filePublished` / `_catalogRegistered` 两阶段状态；发布后重试只执行 Catalog 登记。
- `SemaphoreSlim` 串行化 Syslog 和 H3C Raw 操作。
- 发布后的文件、校验和、实际文件大小在重试中复用，不重新写 Raw。
- Preserve/Flush/Seal 共享内部核心路径，避免重复 Dispose 和旧 `.open` 访问。

## 4. Catalog 登记与恢复语义

PR #18 的 `RegisterFinalizedSegment` 原先直接调用恢复用的 `UpsertRecoveredSegment`。该 Upsert 对同一 `relative_path` 且 `source_id` 相同的记录执行元数据覆盖，可能静默替换 `byte_count`、`sha256`、时间和状态。

临时集成修复将职责分开：

- `RegisterFinalizedSegment`：串行事务登记；同一完整元数据重复登记幂等；来源、状态、记录数、时间、大小、校验和或任务关联不一致时明确抛错并保留旧证据。
- `UpsertRecoveredSegment`：继续仅供恢复流程使用，允许恢复状态机在经过文件验证后更新恢复字段。

这样没有机械覆盖恢复逻辑，也没有改变恢复对损坏尾部的证据保留策略。

## 5. 合并模拟

在独立 worktree 中执行：

1. `main` 合入 PR #1：无冲突。
2. 在该结果上继续模拟合入 PR #5：出现真实内容冲突，文件为：
   - `.github/workflows/p0.yml`
   - `README.md`
   - `src/NetConsole.Next.Service/Catalog.cs`
   - `src/NetConsole.Next.Service/RawSegmentWriter.cs`
3. 未使用 `ours`/`theirs` 整体覆盖关键文件，随后中止模拟合并。

冲突不仅是文本冲突：Raw Writer 的状态机和 Catalog 的登记/恢复职责也已发生语义分叉。不能直接按当前 PR 顺序批量合并。

## 6. 临时集成修复测试证据

临时分支 `audit/p0-g1-stack-integration` 基于 PR #18 HEAD，新增：

- 发布后 Catalog 失败、重复 Seal 和 Raw 字节保留测试。
- 并发 Seal 测试。
- Catalog 元数据冲突拒绝和旧证据保留测试。

已执行：

- .NET Service Release build：通过。
- C# 回归 harness：通过；包含既有 G1/G2/恢复/故障注入/性能回归及新增 Raw 测试。
- Linux 容器 smoke：通过（health、UDP、Raw、SQLite）。
- Vue typecheck：通过。
- Vite build：通过；存在大 chunk warning，但未影响构建。
- Vitest：2 个测试文件、2 个测试通过。
- Playwright：未执行成功，`web/playwright.config.ts` 在该 PR18 树中不存在，属于现有测试入口缺失，不在本次 Raw 修复范围内。

本临时分支未推送时，Windows CI 尚未验证这两个新修复；PR #18 原 HEAD 的 Windows CI 绿色不能替代修复后 Windows 验收。

## 7. 当前阻断评审

当前非过期的 P1 或更高风险评审至少包括：

- PR #5：恢复后的 `.open`/`.ndjson` 在二次崩溃时可能错误标记为 `FINALIZED`。
- PR #7：Windows Service 进程退出码不能保证 SCM recovery 生效。
- PR #8：故障状态下 health 继续访问不可用 Raw 根；SQLite 恢复失败后仍可能启动依赖 Catalog 的 hosted service。
- PR #10：SSH.NET 输出管道上限未真正强制。
- PR #11：持久化异常分类过宽，连接/命令故障可能被误判为持久化故障。
- PR #12：WebSocket 断开不释放会话；终端录制并发 Append；启动恢复和重复挂载问题。
- PR #13：浏览器 reload 后终端会话无法恢复或关闭。
- PR #17：LAN binding 未强制 TLS；仍有 Web auth/CSRF 相关评审。
- PR #18：`FileArchiveService` 使用无界后台队列且缺少任务/用户配额，是当前明确的 ZIP 资源管理 P1。

这些评审没有与本次 Raw/Catalog 修复合并处理，不能宣称整条 Web 链已可合并。

## 8. 依赖安全

各 .NET 分支的 CI 仍报告：

- Package：`SQLitePCLRaw.lib.e_sqlite3`
- Version：`2.1.11`
- Advisory：`GHSA-2m69-gcr7-jv3q`
- 状态：仍受影响，NU1903 High
- 本次决定：不做无关升级；正式发布前单独评估可用修复版本、SQLite ABI 和 Windows 兼容性。

Web 安装报告了 `glob` 旧版本弃用提示；本次未升级 Web 依赖。

## 9. 推荐合并策略和回退

推荐方案 B/C：

1. 先由维护者批准并合入 PR #1。
2. 在 PR #1 最新 HEAD 上，以非改写历史的合并提交同步 PR #5；逐个解决 Raw/Catalog/CI 冲突并重新运行 P0/G1 回归。
3. 只有 PR #5 的 P1 恢复问题关闭后，才继续按 #6 → #7 → #8 → #9 → #10 → #11 的顺序更新和验收。
4. G2.3/ Web 链继续逐个更新，每个 PR 合并前必须检查上游 Raw/Catalog 语义和当前 PR 的未解决评审。
5. PR #18 的 ZIP 队列 P1 必须独立修复和复审，不能用本次 Raw 修复抵消。

回退策略：每一步保留原 PR 合并提交和 CI 运行链接；若出现 Raw 文件、Catalog 或恢复回归，回退最近一个堆叠合并提交，保留已发布 Raw 文件，不删除或重写数据目录。

## 10. 机器可读结论

```text
TASK=NETCONSOLE_NEXT_P0_G1_G2_WEB_STACK_INTEGRATION_AUDIT

REPOSITORY=wxj183589/NetConsole-Next
PR1_HEAD=bc03920092493e36611b634c6119d76203c13fb7
PR1_CI=PASS (run 40094048782 Ubuntu/Windows)
PR1_REVIEW=NO_MAJOR_ISSUES; unresolved threads=0
PR1_MERGED=NO

STACK_PR_COUNT=15 (PR 1,5-18)
STACK_BRANCHES_AUDITED=1,5,6,7,8,9,10,11,12,13,14,15,16,17,18
STACK_ANCESTRY_STATUS=PR5 starts at 0654f0d; PR6-18 descend from PR5; PR1 post-fixes are absent
STACK_DIVERGENCE_FOUND=YES (PR5; current merge simulation conflicts)

P0_FIXES_PRESENT_IN_G1=NO (official PR5-9 heads)
P0_FIXES_PRESENT_IN_G2=NO (official PR10-16 heads)
P0_FIXES_PRESENT_IN_WEB_PR1=NO (official PR17 head)
P0_FIXES_PRESENT_IN_WEB_PR2=NO (official PR18 head; local audit patch restores semantics)

RAW_SEAL_REGRESSION_FOUND=YES (PR18 RawSegmentWriter)
CATALOG_CONFLICT_REGRESSION_FOUND=YES (RegisterFinalizedSegment reused recovery Upsert)
RAW_CONCURRENCY=FIXED_IN_LOCAL_INTEGRATION_WORKTREE
RAW_CONTENT_PRESERVED=PASS (new retry/concurrency tests)
G1_RECOVERY_PRESERVED=PASS in existing PR18 harness; full chain blocked by PR5 recovery P1
H3C_RAW_PRESERVED=PASS in existing harness and shared writer state-machine patch

INTEGRATION_STRATEGY=Sequential non-rewriting merge after PR1 approval; refresh PR5 first; validate in temporary worktree
INTEGRATION_SIMULATION=PR1 merge clean; PR5 merge conflicts in workflow/README/Catalog/Raw; stopped safely
MERGE_CONFLICTS=YES
SEMANTIC_CONFLICTS=YES (Raw seal state and Catalog registration/recovery ownership)
BLOCKING_REVIEWS=PR5,7,8,10,11,12,13,17,18 active P1; PR18 ZIP queue P1 remains independent

P0_TESTS=PASS in PR1 CI; PASS in local PR18 integration harness
G1_TESTS=PASS in existing PR18 harness; not full-chain verified
G2_TESTS=PASS in existing PR18 harness; no real device
WEB_PR1_TESTS=typecheck/build/Vitest PASS; Playwright config missing
WEB_PR2_TESTS=typecheck/build/Vitest PASS; archive P1 unresolved

LINUX_BUILD=PASS (local Docker; CI branch checks PASS)
WINDOWS_BUILD=PASS for official PR heads; PATCHED_TEMP_BRANCH=NOT_RUN_ON_WINDOWS_CI
GITHUB_CI=All listed PR branch checks green; no combined PR1-to-Web run exists

FIX_BRANCH=audit/p0-g1-stack-integration
FIX_COMMITS=PENDING_COMMIT
PUSHED_CHANGES=NO (temporary local integration only)
MAIN_CHANGED=NO
PRS_MERGED=NO
FORCE_PUSH=NO
PRODUCTION_DATA_MUTATION=NO

SAFE_MERGE_ORDER=PR1 approval -> refresh/fix PR5 -> PR6 -> PR7 -> PR8 -> PR9 -> PR10 -> PR11 -> PR12 -> PR13 -> PR14 -> PR15 -> PR16 -> PR17 -> PR18
REQUIRED_MANUAL_APPROVAL=PR1 merge and every subsequent PR merge; Windows Service/legacy OS/real devices remain manual
NEXT_ACTION=Review local fix; decide whether to push as a child fix PR for PR18, then update PR5 from PR1
FINAL_STATUS=INTEGRATION_FIX_REQUIRED
```
