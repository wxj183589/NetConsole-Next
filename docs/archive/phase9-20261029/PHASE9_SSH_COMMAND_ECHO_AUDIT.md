# Phase 9 SSH 命令回显审计

## 已验证规则

1. 登录/命令提示符支持 `NAME#`、`NAME>`、`<NAME>` 和配置模式变体。
2. 仅删除命令前导 Echo；正文中再次出现同名文本会保留。
3. ANSI 控制序列和退格先规范化，再识别分页/提示符。
4. `---- More ----`、`Press any key/space to continue` 进入 `PAGED`，不会按删首尾行处理。
5. 有闭合提示符才视为完整交互 transcript；缺失闭合提示符为 `INCOMPLETE`。
6. 不支持命令、权限不足、stderr、非零退出、断线、超时、取消和输出上限都保留 partial prefix 或结构化失败码。
7. UTF-8、GB18030、AUTO 均有编码单测；跨 transport read 的多字节字符不会插入替换符。
8. 输出上限为 256 KiB，超限会 abort transport；`Dispose/Abort` 释放会话并更新活动计数。

## 仍缺真实证据

当前证据来自 SSH.NET wire simulator、脱敏 fixture 和离线回放；尚未用真实 H3C V7/V9、ZTE C89E-4 设备核验交互 Shell 的分页续页、长输出终止、GB18030 实际协商、设备重连和现场权限文本。Next 当前仍使用 Exec Channel，未因旧项目经验自动开启交互 Shell。
