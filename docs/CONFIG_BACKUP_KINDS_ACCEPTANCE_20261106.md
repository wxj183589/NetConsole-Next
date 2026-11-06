# H3C 三类只读配置备份验收

日期：2026-11-06，最终候选复核2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。开发基线：`d0aaee6`；功能提交 `e93b382`、下载no-store修复 `ef57213`。本文件补充 [设备入口与差异验收](CONFIG_BACKUP_DEVICE_DIFF_ACCEPTANCE_20261106.md)，不将历史测试结果作为本批结果。整体产品及实际设备兼容仍为 `PARTIAL / REAL_DEVICE_PENDING`。

## 类别与只读边界

| API 类别 | 页面名称 | 唯一采集命令 | 内容语义 |
| --- | --- | --- | --- |
| `RUNNING` | 运行配置 | `display current-configuration` | 当前运行配置 |
| `SAVED` | 保存配置 | `display saved-configuration` | 设备报告的已保存配置正文 |
| `STARTUP_INFO` | 启动配置指向 | `display startup` | 启动文件指向信息，允许设备返回 `NULL` |

启动配置指向不包含启动文件正文，也不表示已经具备配置恢复能力。页面明确显示此说明。备份统一复用现有任务、CPython/Netmiko、Profile 凭据、Site scope、Raw 与版本文件，不增加设备连接体系。

三类均只查询。没有迁移原版独立的 `save force` 写操作，也不发送 `save`、`system-view`、恢复或配置修改命令。FIT-AP 继续由 AC 管理，不能作为独立备份目标。首次打开页面、切换类别、读取历史、比较和下载均不重新采集；只有点击“开始配置备份”才创建采集批次。

只读参考原版：`D:\study\NetConsole-Workspace\NetConsole\src\netconsole\services\config_lifecycle_service.py:54-56` 分开定义运行、保存与写入命令，`:142-178` 通过统一 Netmiko 获取配置；`config_text.py:109-140` 提供正文提取与完整性处理参考。

## API、版本与旧记录

- `POST /api/system/config-backups` 接受可选 `configurationKind`；缺省为 `RUNNING`。非法类别返回 `400 CONFIG_BACKUP_KIND_UNSUPPORTED`，不创建采集任务。
- `GET /api/system/config-backups?profileId=...&configurationKind=...` 按类别筛选；不传类别时保留合并历史兼容性。各类别独立编号，交错采集不会占用其他类别的版本号。
- 新 manifest 同时保存类别和确切命令；类别与命令不匹配的记录不提供列表、下载或差异。新下载文件名包含 `.running.cfg`、`.saved.cfg` 或 `.startup_info.cfg`。
- 旧 manifest 缺少类别及命令时按运行配置读取，兼容原 `id.cfg` 文件，不迁移或重写原文件。
- Diff 必须属于同一 Profile 和同一类别；跨类别返回 404。UI 同时筛掉混入其他类别的记录，并阻止比较同一个版本。
- 保存配置不存在、权限不足、命令不支持、残留分页、空输出或失败/中断采集，不生成成功的保存配置快照。缺少保存配置时返回 `CONFIG_BACKUP_SAVED_UNAVAILABLE` 并显示中文原因。

## 脱敏与 Raw 边界

调度器在 Raw 持久化前调用同一配置脱敏器，快照写入再次调用。三类配置均执行这些规则，配置命令仍从普通 Facts 与诊断附件下载入口排除。

逐行规则与诊断敏感词集合一致：`password`、`passwd`、`passphrase`、`pass-phrase`、`pre-shared-key`、`community`、`private-key`、`secret`、`credential`、`authorization`、`token`、`psk`。敏感值替换为 `<REDACTED>`，普通配置行保留。

PEM 私钥先于逐行规则整体脱敏，覆盖普通、RSA、EC、OPENSSH、ENCRYPTED `PRIVATE KEY`；完整块从 BEGIN 至 END 一并替换，缺少 END 的截断块从 BEGIN 替换至输出末尾。规则大小写不敏感，并有一秒正则超时。不能只替换 PEM 头行而留下私钥正文。本批不扫描或改写历史备份文件。

`VerifyPrivateKeyRedaction` 覆盖三类 Raw/下载、仅秘密变化的 Diff、五种完整及截断 PEM、全部敏感词和非配置命令边界。该组已在最终完整Service harness session23456执行，配置备份9/9 PASS。

成功下载响应设置 `Cache-Control: no-store`。首轮离线候选发现缺少该响应头，ef57213修复后配置HTTP28及离线候选三类下载断言均通过。

## 页面交互

设备授权与连接资料读取完成后，历史读取用 `historyLoading` 独立管理，可在首个历史请求尚未返回时切换类别。旧类别的晚到历史、差异和下载响应按序列号丢弃；旧历史完成不会解除后续备份的执行 busy 状态。Site 撤权、设备关联不一致、模块关闭与厂商不支持时清除历史和执行入口。

## 本批实际验证

| 验证 | 实际结果 | 证据边界 |
| --- | --- | --- |
| `tests/config_backup_e2e.py` | **28 checks PASS**，主控执行并确认 | 实际 HTTP Service → CPython/Netmiko → 回环合成 SSH；含三类独立编号、同类差异、跨类拒绝、缺少保存配置、无写命令及历史/差异/下载不重采集 |
| `node node_modules/vitest/vitest.mjs run src/views/ConfigBackupsView.test.ts`，工作目录 `web` | **1 file / 37 tests PASS** | 含类别切换、混类过滤、晚到响应、同类比较及 busy 归属回归 |
| Web 全套 Vitest | **14 files / 140 tests PASS** | 本批代码的实际执行结果，不代表真实设备兼容 |
| `node tests/device_backup_browser_e2e.mjs` | **9 scenarios PASS** | Chrome 源码 Vite UI + API mock；无页面错误、无未知 API |
| C# 配置备份 harness | **9/9 PASS**，最终完整 Service harness session23456 EXIT0 | 包含完整/截断私钥、manifest 兼容、独立编号与同类别差异断言；Release Service/Tests 构建 0 warnings/errors |
| Python AST、浏览器脚本 Node 语法、`git diff --check` | **PASS** | 语法和补丁静态检查；不替代上述运行验证 |

浏览器覆盖精确设备详情入口、三类显式开始、过滤混类 `v99`、同类比较、相同版本禁用、类别切换/比较不提交采集、无效设备/不支持厂商/模块关闭无回退、ADMIN 只读及撤权清空。报告类型为 `CHROME_SOURCE_UI_MOCKED_API_FIXTURE`，不是浏览器到真实设备的端到端采集。

已实际查看三张 1920×1080、DPR1 截图：三类标签、版本历史及比较控件正常显示；启动文件指向说明完整可见，未发现内容遮挡或水平溢出。截图中的设备、时间和 SHA 为合成 fixture。

证据统一保存在 `D:\study\NetConsole-Next-Workspace\build\device-backup-browser\`：

- `acceptance.json`
- `device-b-config-backup.png`
- `device-b-saved-backup.png`
- `device-b-startup_info-backup.png`

## 数据影响与剩余验证

HTTP 使用 Next Workspace 内的隔离合成数据与回环 SSH；浏览器使用 mock API，不写设备或数据库。未访问旧 NetConsole 数据根，未操作 Production/Dev 真实业务数据。浏览器进程与临时 Vite server 已退出，保留上述可复查截图与 JSON。

后端新增 `9/9` 已由主控在最终完整 Service harness 中执行通过。现场 H3C 交换机、路由器、AC、不同型号和实际启动文件内容仍需对应证据，继续标记 `REAL_DEVICE_PENDING`；Windows 离线候选运行结果见统一架构验收。保留当前分支，只做本地提交，不 Push、Merge 或 Release。
