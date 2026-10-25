# NetConsole Next 主分支安全合并评估

日期：2026-10-26

任务：`NETCONSOLE_NEXT_SAFE_MAIN_INTEGRATION_AND_LOCAL_PREVIEW`

仓库：`wxj183589/NetConsole-Next`

当前远端主分支：`9c544d2e4c404015e732fdcfd05a031636ee1db2`

## 1. 结论

```text
MAIN_MERGE_RECOMMENDATION=BLOCKED
MAIN_CHANGED=NO
PRS_MERGED=NO
FORCE_PUSH=NO
OPEN_PR_COUNT=16
```

当前不能安全合并到 `main`。原因不是 CI 全部失败，而是堆栈中仍有未解决的 P1/P2 评审问题，PR6 当前不可合并，且 PR17 仍有涉及 LAN/TLS 安全边界的 P1。所有 PR 的评审状态均未取得 APPROVED；现有评论不能替代正式批准。

## 2. 审计时的 PR 堆栈

依赖顺序是：

```text
main
  -> #1 -> #5 -> #6 -> #7 -> #8 -> #9 -> #10 -> #11 -> #12
  -> #13 -> #14 -> #15 -> #16 -> #17 -> #18 -> #19
```

| PR | base -> head（摘要） | Mergeable | 未解决线程 | CI |
| --- | --- | --- | ---: | --- |
| #1 | `main` -> `bc039200` | true | 0 | PASS |
| #5 | #1 -> `92a458c9` | true | 0 | PASS |
| #6 | #5 -> `36153254` | false | 2 | PASS |
| #7 | #6 -> `9c574ab5` | true | 1 | PASS |
| #8 | #7 -> `1dcbea1f` | true | 3 | PASS |
| #9 | #8 -> `a1f8bdd0` | true | 3 | PASS |
| #10 | #9 -> `e41f8733` | true | 2 | PASS |
| #11 | #10 -> `82f2a052` | true | 2 | PASS |
| #12 | #11 -> `27174402` | true | 5 | PASS |
| #13 | #12 -> `cafd9cab` | true | 2 | PASS |
| #14 | #13 -> `bf311cee` | true | 4 | PASS |
| #15 | #14 -> `439ee27c` | true | 1 | PASS |
| #16 | #15 -> `293abdf4` | true | 0 | PASS |
| #17 | #16 -> `019e7547` | true | 3 | PASS |
| #18 | #17 -> `c3ec4ba6` | true | 0 | PASS |
| #19 | #18 -> `d07cdb4c` | true | 0 | PASS |

上表的 `PASS` 仅表示对应 GitHub Actions 检查在审计时成功，不表示评审线程已解决，也不表示 Windows GUI、真实设备或正式数据验收已完成。

## 3. 阻塞项与数据完整性关注

- **PR6**：`mergeable=false`；未解决问题包括任务超时应从 submit 时开始计算，以及取消排队任务必须回收容量。
- **PR7**：Windows Service 在 SCM recovery 场景必须返回非零退出码。
- **PR8**：诊断 health 路径仍读取无权限 Raw 根；关键 SQLite 恢复后不能启动依赖 Catalog 的 hosted service；状态转换持久化失败不能被吞掉。
- **PR9**：历史线程包含 flush deadline、保留其他 degradation reason、维护审计成功后的失败状态清理问题；部分评论已过时但应由作者明确关闭。
- **PR10**：SSH.NET `PipeStream` 输出上限未强制执行；任务定义 snapshot 与执行时定义存在不一致风险。
- **PR11**：设备 IOException/UnauthorizedAccess 不应误报为 persistence unavailable；Windows token 文件 ACL 加固不能依赖安装器单一路径。
- **PR12**：WebSocket 断开后的 terminal session 清理、recording append 串行化、live terminal row 恢复、第二个 WebSocket 拒绝、host-key rejection 翻译仍有未解决线程。
- **PR13**：页面 reload 后 terminal session 残留；无网络设备时 COM action 不应消失。
- **PR14**：replay search/seek、serial auth 误记录、导出失败字节数等问题仍有未解决线程。
- **PR15**：replay cursor 不应信任过期 count。
- **PR17**：CSRF rotation、LAN bind 必须要求 TLS、LAN enabled 但未 bind 时仍只能 loopback；其中 LAN/TLS 是当前明确的 P1 阻塞项。

Raw/Catalog 方向的修复不能覆盖这些阻塞项：PR1 的早期 Raw P1 和 PR5 的相关线程已解决；PR18 处理了 bounded ZIP queue/disk 的 P1；PR19 继续修复 Raw seal/Catalog 语义，但它仍建立在整条前置堆栈上，不能绕过前置 PR 的未解决评审。

## 4. 合并模拟状态

```text
STACK_MERGE_SIMULATION=PARTIAL_BLOCKED_REMOTE_FETCH
STACK_MERGE_BLOCKERS=PR6 mergeable=false; unresolved review P1/P2; PR17 LAN/TLS P1; terminal GitHub fetch unavailable
```

没有声称完成精确的本地全栈 merge simulation：终端 Git fetch/HTTPS 连接 GitHub 失败，且本地缺少 PR5 的完整远端 ref。为提供 Windows 预览，已在隔离工作树中按 PR17、PR18、PR19 使用 GitHub 只读补丁完成内容物料化，但这不是从 `d07cdb4caaaf9942f560390279d407627037f613` 精确 checkout 得到的 Git 历史。

本地预览记录：

```text
UPSTREAM_PREVIEW_BRANCH=audit/p0-g1-stack-integration
UPSTREAM_PREVIEW_HEAD=d07cdb4caaaf9942f560390279d407627037f613
LOCAL_PREVIEW_BRANCH=codex-A/next-local-preview-20261026
LOCAL_PREVIEW_HEAD=293abdf4fbbfb47af4827d6445624f9fe531cbe8
```

## 5. 推荐后续顺序

1. 先由各 PR 作者处理并关闭 #1、#5 以及后续堆栈的 review threads，并取得正式批准。
2. 先修复 #6 的 mergeability、超时起点和队列容量回收，再按依赖顺序逐 PR 推进。
3. 在 #7/#8/#11 等边界问题关闭前，不能把 Windows recovery、权限故障和 Catalog 可用性报告为安全。
4. 在 #12/#13/#14/#15 的 terminal/replay 生命周期问题关闭前，不能把终端与回放报告为现场可用。
5. 必须先关闭 #17 的 LAN/TLS P1，再评估 #18/#19 的最终 Raw/Catalog 合并。
6. 获得用户明确批准后，按堆栈顺序使用普通 Merge Commit 保留 ancestry；不使用 squash、ours/theirs 或 force push。

本次任务只完成审计、隔离 Windows 预览脚本和证据记录，没有替用户合并 PR、修改 `main` 或推送任何远端分支。
