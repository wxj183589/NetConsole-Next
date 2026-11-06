# R4 Recovery Report

日期：2026-11-07（Asia/Shanghai）

## 状态

本轮从 `ad3a574` 继续，不重置、不清理、不覆盖既有修改。主工作区 clean；上一轮完成的 R3/续跑功能已在当前产品提交图中。整体状态仍 `PARTIAL / REAL_DEVICE_PENDING`。

## 保护结果

- 45 个 worktree 已登记；4 个 dirty worktree 已保留，未修改。
- 旧 autonomous worktree `816e144` 已确认存在。
- 旧 NetConsole 真实数据目录只做目录级核对，没有读取、复制或修改数据库。
- 四台白名单模拟器尚未执行本轮配置写入。
- Push、remote merge、release 均未执行。

## R4 当前工作流

| 工作流 | 分支 | 状态 |
| --- | --- | --- |
| AP identity lifecycle | `codex-A/r4-identity-lifecycle` | 编码中 |
| Configuration restore | `codex-A/r4-config-restore` | 编码中 |
| Scheduler/connection/simulator evidence | `codex-A/r4-scheduler-evidence` | 检查与编码中 |

## 外部边界

真实双 AC HA、主备切换、实时 AP 归属、目标机安装、跨机器 TLS 和原生 Windows 125% 不能由当前离线 fixture 或历史报告替代。模拟器测试只连接用户明确列出的四个地址；没有安全存储凭据时标记 `BLOCKED_CREDENTIALS`，不猜测密码。

## 下一检查点

各 R4 worktree 返回提交后，主协调工作流将按“审阅 diff → 定向测试 → Service/前端回归 → 本地合并 → 更新状态文件 → 更新 DevStatus”顺序处理，并生成 R4 模块矩阵、缺口和推荐报告。
