# H3C 全机诊断采集与下载验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。本文件记录当前增量的源码、合成 SSH 和浏览器验证；真实设备为 `REAL_DEVICE_PENDING`。保留当前分支，不 Push、Merge、Release。

## 继承与实现

参考原版 `src/netconsole/services/diagnostic_download_service.py` 的 `display diagnostic-information` 采集流程，复用 Next 已有的任务、保存凭据、CPython + Netmiko、Raw 封存及任务诊断 TXT / ZIP 下载链路。没有增加网络设备 SSH/Telnet 客户端或独立认证路径。

原版按固定顺序发送 `screen-length disable`、诊断命令和 `n`。Next 由 Netmiko 负责分页和通道读取，仅在本次输出末尾出现下列已知问题时发送一次 `n`，直接完成时不发送答案：

```text
Save or display diagnostic information (Y=save, N=display)? [Y/N]:
```

该单一措辞接受大小写和空白变化；措辞本身只有合成测试覆盖，没有官方文档或真实固件回显证据。未知问题不作答，按取消或超时结束。不会发送 `y`、`save` 或设备配置命令，也不会将 `n` 注册为普通任务命令。真实固件出现其他问题时需要先取得证据，再决定是否增加受支持措辞。

Netmiko 的 `find_prompt()` 提供完整终端提示符。诊断通过 Netmiko `send_command()` 等待已知问题或该完整提示符，避免 Comware 去掉尖括号后的 base prompt 在正文 `sysname` 中提前命中；没有自建通道读取、分页或提示符发现循环。

## 任务与证据边界

- 任务中心提供独立的“采集全机诊断”按钮，只提交 `display diagnostic-information`，使用设备已保存的凭据。该命令不混入普通采集复选框。
- 任务总时限为 `600000 ms`。Worker 的两次命令读取共享 `540 s` 的单调时钟期限；未知问题、断线和取消均通过现有 Worker 生命周期释放连接。
- 诊断原始回显上限 `4 MiB`，对应 NDJSON 记录上限 `6 MiB`。普通命令仍使用 `256 KiB` / `512 KiB` 上限。超限失败，不静默截断或标为成功；ZIP 包上限仍为 `8 MiB + 128 KiB`。
- 诊断结果未知时不自动重新发送诊断命令。直接完成、已知问题、未知问题、断线、取消、不支持和超限场景均检查没有自动重放。
- 必须观察到结束提示符才认可完整回显；不支持、权限拒绝、分页残留、空输出、命令边界错误和缺少结束提示符分别保留失败分类。
- 保存前对已识别的密码、口令、community、token 等敏感行及私钥块脱敏，下载继续执行防御脱敏。测试覆盖 password 和私钥内容不进入封存诊断正文或下载。
- `<REDACTED>` 与 Comware `<prompt>` 外形相似。诊断解析时将精确占位符视作正文，Raw / TXT 保持既有占位符；定向回归验证脱敏后正文不会被误判为关闭提示符或命令边界错误。
- parsed JSON 仅保存完成状态、字节数和失败分类，不重复多 MiB 诊断正文，不将诊断或其中的配置内容发布为设备事实。Raw 仍是证据 Authority。
- TXT / ZIP 只读取已有封存记录；下载和服务重启后的下载不重新连接设备。原有完整性、权限、Site、任务归属和流中撤权检查继续适用。

本次对全机诊断的大小边界补充了既有 `TASK_DIAGNOSTIC_DOWNLOAD_ACCEPTANCE_20261106.md` 中普通命令的 `256 KiB` 限制；普通命令限制没有放宽。

## 已执行验证

| 检查 | 结果与证据范围 |
| --- | --- |
| Release Service / Tests build | PASS，0 warnings / 0 errors；当前诊断占位符修复后重新构建 |
| `--h3c-diagnostic-capture-tests` | `30/30 PASS`；只读策略、不自动重放、完整性状态、错误命令、脱敏与占位符回归、大小边界 |
| `--task-diagnostic-evidence-tests` | 最新 `96/96 PASS`，session `33510`，EXIT 0；含大于普通上限的封存/TXT/ZIP、恰好 4 MiB、孤立 segment 扫描恢复与重建索引、下载一致性、超限拒绝及原有证据/权限回归 |
| `py -3.13 tests/h3c_diagnostic_e2e.py` | `24 checks PASS`，session `5205`，EXIT 0；真实 HTTP → CPython/Netmiko → 合成回环 SSH，直接/已知确认、大回显、未知问题、断线、取消、不支持、超限、保存前脱敏、重启下载和资源清理 |
| `node tests/task_diagnostics_browser_e2e.mjs` | `9/9 scenarios PASS`，session `31777`，EXIT 0；真实 Chrome / 源码 Vite / 模拟 API，TXT/ZIP、全机诊断资格、权限失效与迟到隔离、独立按钮的单命令和十分钟请求；无额外密码字段 |
| 浏览器截图 | 已逐张检查 4 张截图；1920×1080 与 2560×1440 对话框完整，表格无横向溢出，校验失败与撤权提示清楚，失效后下载入口关闭 |
| 静态检查 | 浏览器脚本 `node --check`、`git diff --check` PASS |

HTTP 测试模拟器允许 Netmiko 的 `quit` / `exit` 清理命令关闭通道，并保留命令记录；不会把断开连接当作对未知问题的答案。测试只使用合成密码和私钥样本。

浏览器证据位于 `D:\study\NetConsole-Next-Workspace\build\task-diagnostics-browser\acceptance.json`，类型为 `CHROME_SOURCE_UI_MOCKED_API_FIXTURE`。4 张截图为 `diagnostics-dialog-1920.png`、`diagnostics-dialog-2560.png`、`diagnostics-integrity-failed.png` 和 `diagnostics-scope-revoked.png`。该证据不表示后端或真实设备兼容验证；后端链路由上述独立 HTTP 测试覆盖。

离线候选检查可复用 `tests/h3c_diagnostic_e2e.py` 的 `DiagnosticTarget` 与 `verify_capture()`；后者使用调用者已有的 Service 和 bundled Python / Worker，不替换运行环境，包含直接与确认分支共 12 项检查。实际候选结果由集成验收记录另行报告。

## 数据影响与待验收

仅使用 Next Workspace 内的隔离 SQLite、Raw 和合成回环 SSH。HTTP 测试自有 Service、Worker、SSH 均已停止，临时数据已退役；浏览器自有 Vite 和 Chrome 已关闭。未访问或复制旧 NetConsole 的业务数据根，没有执行真实设备配置变更。

H3C 交换机、路由器和 AC 的真实固件提示词、实际诊断大小、耗时及网络中断表现仍为 `REAL_DEVICE_PENDING`。合成 fixture 成功不能替代现场兼容验收。
