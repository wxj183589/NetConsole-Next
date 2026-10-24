# PR Stack Integration Review

审计日期：2026-10-24（Asia/Shanghai）  
仓库：`wxj183589/NetConsole-Next`  
审计分支：`audit/p0-g1-stack-integration`，基于 PR #18 最新 HEAD `c3ec4ba6862de8e90630552e3e2410f509787f15` 的临时 worktree；PR #18 在审计期间持续更新，已逐次重新同步。

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
| 5 | `323aca12ec72` | `#1@bc0392009249` | `bc0392009249` | merge commit | 是（同步后） | MERGEABLE/CLEAN | Ubuntu/Windows PASS |
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
| 18 | `c3ec4ba6862d` | `#17@019e75479756` | `019e75479756` | 更新中 | 否（相对 PR5 新 HEAD） | MERGEABLE/UNSTABLE | Ubuntu PASS；Windows 运行中后通过 |

| 19 | `69bb7d6294e` | `#18@c3ec4ba6862d` | `c3ec4ba6862d` | 本地集成 | 包含 PR1/PR18 修复 | MERGEABLE/CI pending | [PR #19](https://github.com/wxj183589/NetConsole-Next/pull/19) |

PR #5 已在独立 worktree 中以非改写 merge commit `323aca12ec72` 同步 PR #1，当前远程 PR #5 为 CLEAN；其 CI 已完成 Ubuntu/Windows PASS。PR #6–#18 的官方 head 尚未同步该新 PR5，因此不能由其“绿色 CI”推导出已包含 PR #1 修复。

| 19 | `69bb7d6294e` | `#18@c3ec4ba6862d` | `c3ec4ba6862d` | 本地集成 | 包含 PR1/PR18 修复 | MERGEABLE/CI pending | [PR #19](https://github.com/wxj183589/NetConsole-Next/pull/19) |


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

PR #18 最新 HEAD `c3ec4ba` 已包含 bounded archive queue、磁盘准入、取消清理、Windows auth/SQLite cleanup 和对应回归测试。PR #19 仍需同时审查 Raw 状态机和堆叠同步结果。

PR #19 的 Raw 审计确认 PR #18 之前的 Raw 实现存在两项与 PR #1 同类的风险：

1. `SealAsync` 在 `.open` 移动为 `.ndjson` 后才执行 Catalog 登记，登记失败后重复 Seal 可能再次访问不存在的 `.open`。
2. Append、Flush、Seal、`AppendH3cCommand` 没有共同操作门，多个 Seal 或 Append/Seal 并发时可能竞争文件和状态。

临时集成修复保留现代缓冲、H3C 输出和性能计时，并增加：

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

## 5. 阶段 A 与阶段 B 集成结果

### 阶段 A：PR #19 Raw 状态机与最新 PR #18

- PR #19 原始 head：`f4cbd45d7cdba30962dd695b0b54c1b479ede04f`。
- Raw P2 修复提交：`4e6eeff`；PR #18 bounded archive/disk 修复同步：`fc40031`；随后同步 PR #18 Web 回归 head `9473365`、`c3ec4ba`，当前 PR #19 head 为 `69bb7d6`。
- `PreserveOpenAsync()` 关闭后，Append、H3C Append、Flush、Seal 均拒绝；Flush 失败会保留 `.open` 并显式传播异常。
- 已验证 Catalog 登记失败后的仅 Catalog 重试、重复 Seal、并发 Seal、Preserve/Seal 竞争和 Raw 字节不变。
- PR #19 与 PR #18 的每次同步均使用 `--no-commit --no-ff`；最新同步无未解决文件冲突，PR #18 bounded ZIP、磁盘准入、取消和清理测试均保留。

### 阶段 B：PR #5 与最新 PR #1

- PR #5 原始 head：`6a8906a235e920078790b6ac4eeded02bcaf52d1`。
- 实际冲突文件：`.github/workflows/p0.yml`、`README.md`、`Catalog.cs`、`RawSegmentWriter.cs`；均逐文件语义解决。
- 新 head：`323aca12ec72c9c817e8ca019b49a270c37d638f`，为父分支 `6a8906a` 与 PR #1 `bc039200` 的非改写 merge commit。
- G1 P1 修复：增加 `raw_recovery_intents` 持久化恢复意图；在 `.open` 重命名和尾部修复前记录意图；二次启动按 `INCOMPLETE` 处理；Catalog 登记与恢复 Upsert 分离。
- PR #5 当前 GitHub 状态：MERGEABLE/CLEAN；Ubuntu、Windows 两组 CI 均 PASS；未自动合并、未 Resolve 原 P1 review thread。

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

PR #19 的首次 Actions run `40097847169` 在 Ubuntu 的 `tests/web_pr2_e2e.py` 归档任务取消断言处失败，Windows job（含 Windows Service SCM）通过；随后最新 run [40098552288](https://github.com/wxj183589/NetConsole-Next/actions/runs/40098552288) 的 Ubuntu 和 Windows 均成功。该首次失败发生在 Raw、G1 recovery、SSH protocol、Web contract 和构建均通过之后，属于 PR #18 已知 ZIP 队列 P1/测试敏感性，不是本次 Raw/Catalog 修复的失败。

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
- PR #18：原 bounded queue 与磁盘准入 P1 已在最新 head 修复并完成 Codex 复审；ZIP64 超大文件、真实 Windows GUI/Explorer 和 LAN 实机验收仍未运行。

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

1. 先由维护者批准并合入 PR #1；本轮未执行合并。
2. 审查 PR #5 的 `323aca12ec72`，重点确认双次崩溃恢复 P1 review thread；CI 已通过，复审未完成前不 Resolve。
3. PR #5 获批准后，逐个刷新 PR #6→#18；官方后代当前均未包含 `323aca12ec72`，不能批量合并或推断自动继承修复。
4. PR #19 作为 Raw 状态机与 PR #18 最新 Web/ZIP 代码的独立审计载体；合并前需先将 base 固定到最终 PR #18 head 并完成新一轮 review。
5. 任一步出现 Raw 文件、Catalog 或恢复回归，回退最近的堆叠合并提交，保留 Raw 和恢复证据，不删除或重写数据目录。

## 10. 机器可读结论

```text
TASK=NETCONSOLE_NEXT_PR19_RAW_STATE_P2_AND_PR5_INTEGRATION
REPOSITORY=wxj183589/NetConsole-Next
PR1_HEAD=bc03920092493e36611b634c6119d76203c13fb7
PR1_STATUS=READY_FOR_MERGE_REVIEW
PR1_MERGED=NO

PR18_HEAD=c3ec4ba6862de8e90630552e3e2410f509787f15
PR18_CI=Ubuntu PASS; Windows run after latest auth cleanup; review threads resolved
PR18_REVIEW_STATUS=Codex review clean on bounded queue/disk admission; real ZIP64/Windows GUI/LAN pending

PR19_HEAD_BEFORE=f4cbd45d7cdba30962dd695b0b54c1b479ede04f
PR19_HEAD_AFTER=69bb7d6294ee364e1146f94ec9b620d40fb8d88e
PR19_P2_ROOT_CAUSE=Append/Flush/Seal accepted operations after PreserveOpenAsync closed the stream
PR19_P2_FIX_SUMMARY=Explicit Raw state machine, serialized operation gate, preserved-open failure propagation, retry-only Catalog registration
APPEND_AFTER_CLOSED_REJECTED=PASS
H3C_APPEND_AFTER_CLOSED_REJECTED=PASS
FLUSH_AFTER_CLOSED_REJECTED=PASS
SEAL_AFTER_PRESERVE_REJECTED=PASS
CATALOG_RETRY_AFTER_PUBLISH=PASS
CONCURRENT_SEAL=PASS
RAW_CONTENT_PRESERVED=PASS
NO_FALSE_FINALIZED=PASS
CATALOG_CONFLICT_REJECTED=PASS
PR19_UPDATED_FROM_PR18=YES (latest observed c3ec4ba; no unresolved merge conflict)
PR19_CONFLICTS_RESOLVED=YES
PR19_RAW_REGRESSION=PASS
PR19_G1_REGRESSION=PASS
PR19_G2_REGRESSION=PASS in existing harness; no real device
PR19_WEB_PR2_REGRESSION=PASS locally and in GitHub run 40107064393
PR19_REVIEW_RECONFIRMED=PENDING on latest 69bb7d6
PR19_P2_THREAD_RESOLVED=NO (thread is outdated but deliberately unresolved pending latest review)

PR5_HEAD_BEFORE=6a8906a235e920078790b6ac4eeded02bcaf52d1
PR5_HEAD_AFTER=323aca12ec72c9c817e8ca019b49a270c37d638f
PR5_UPDATED_FROM_PR1=YES (non-rewriting merge with bc039200)
PR5_MERGE_CONFLICTS=.github/workflows/p0.yml; README.md; Catalog.cs; RawSegmentWriter.cs
PR5_P1_ROOT_CAUSE=Recovered .open renamed before durable Catalog recovery intent, so second startup could promote orphan .ndjson to FINALIZED
PR5_P1_FIX_SUMMARY=Durable raw_recovery_intents plus atomic INCOMPLETE registration/intent cleanup; strict normal Seal Catalog registration
RECOVERED_OPEN_PRESERVED_INCOMPLETE=PASS
CRASH_AFTER_RENAME=PASS
CRASH_AFTER_TAIL_REPAIR=PASS
SECOND_STARTUP_STATE=INCOMPLETE
RECOVERY_IDEMPOTENT=PASS
SQLITE_FAILURE_RECOVERY=PASS
CATALOG_NO_DUPLICATE_ROWS=PASS
PR5_P0_REGRESSION=PASS
PR5_G1_REGRESSION=PASS
PR5_GITHUB_UBUNTU_CI=PASS (runs 40106145235/40106148614)
PR5_GITHUB_WINDOWS_CI=PASS (runs 40106145235/40106148614)
PR5_REVIEW_RECONFIRMED=PENDING independent review of 323aca1
PR5_P1_THREAD_RESOLVED=NO

DOWNSTREAM_PRS_AUDITED=6-18 (read-only)
DOWNSTREAM_BASE_UPDATES_REQUIRED=YES; official PR6-18 heads do not contain 323aca12
DOWNSTREAM_RAW_SEMANTIC_RISKS=YES until each child is refreshed and tested
MAIN_CHANGED=NO
PRS_MERGED=NO
FORCE_PUSH=NO
OLD_NETCONSOLE_CHANGED=NO
PRODUCTION_DATA_MUTATION=NO

BLOCKING_P0_COUNT=0 in tested worktrees
BLOCKING_P1_COUNT=PR5 review pending; downstream stale; PR17 TLS/LAN and other active stack findings remain
OPEN_P2_COUNT=PR19 Raw P2 unresolved pending latest review; other child review threads unchanged
NEXT_RECOMMENDED_ACTION=Review PR5 323aca1 and PR19 69bb7d6 independently; refresh PR6 only after PR5 approval
FINAL_STATUS=CI_VERIFIED_REVIEW_PENDING
```
