# 最终交接

工作树：`D:\study\NetConsole-Next-Workspace\worktrees\next-autonomous-full-feature`。Branch：`codex-A/netconsole-next-autonomous-full-feature`。最新产品提交：待本地提交（FD-P2-03 LLDP 关系、变化历史、claims API/UI）；未 push、merge、release。

恢复顺序：读取 `AUTONOMOUS_RUN_STATE.json` → 检查 `git status` → 先取得模拟器安全凭据 → 运行 connection test → 再做双 AC 只读采集与 HA 证据；配置备份和诊断重启持久化仍待后续。不得访问旧 NetConsole 数据根，不得复制整个真实 Site，不得把离线 fixture 当作实时验收。

数据影响：只写 Next Workspace 隔离 fixture/测试目录；真实生产和开发数据均未修改。未完成项见 `REMAINING_BLOCKERS.md`。
