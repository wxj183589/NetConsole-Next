# NetConsole-Next Branch Relationship Report

日期：2026-11-11

## 提交关系

| 参考 | 提交 |
|---|---|
| GitHub `main` | `230b830e623020ad15664fe701c2d9c3c9d1330f` |
| 主工作树分支 `codex-A/netconsole-next-r3-dual-ac` | `84e53533e5197aa4075c53f98cf5fb0775189a05` |
| 两者 merge-base | `4f05d6e565aa7393c57954c2514d409169069aa8` |
| 本阶段分支 `codex-A/device-credential-dpapi-20261111` | `8828d0a4a7f69e50cf24bf437a75c00cd47573e3` |

`git rev-list --left-right --count github/main...codex-A/netconsole-next-r3-dual-ac` 的基线结果为 `18 266`，即远端 `main` 独有 18 个提交，本地 R3 分支独有 266 个提交。两条线不能通过简单的快进关系替代。

## 分支选择

本阶段没有把修改直接放入 R3 主工作树，原因是：

1. PR #48 的目标代码在远端 `main`，而当前 R3 集成线使用了不同的设备凭据/无线演进代码路径。
2. 直接混合会把设备异常修复与 R3 无线集成、既有未跟踪 assets 绑定，增加回滚和审查风险。
3. 独立 feature branch 可以先由 CI/审查验证，再由用户选择合并目标；本阶段没有自动合并 `main`。

## 远端审查对应

GitHub PR #48 的两个未解决审查线程分别对应：

- `src/NetConsole.Next.Service/DeviceManagement.cs:296`：DPAPI 不可用时创建路由返回 500。
- `web/src/views/DeviceEditorView.vue:289`：修改管理地址后不清除已确认的 SSH 主机指纹。

本分支 `8828d0a` 针对这两个线程提交了最小修复；未对已合并 PR #48 做历史变更。

