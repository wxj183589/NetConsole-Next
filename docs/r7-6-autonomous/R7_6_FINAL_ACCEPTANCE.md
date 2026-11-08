# NetConsole-Next R7.6 Final Acceptance

## 2026-11-08 最新集成检查点

本批已完成丢失采样历史上下文、实际Raw完整性复核并部署。产品56d16e3/6e48a0a，夹具与验收d99e103/8cd1e2f/0dd6a14。最新候选build/r76-loss-context-8cd1e2f，ProductVersion1.0.0+8cd1e2f96038acef6c63181ad49cb6d7c4920403，Service18771/PID41856 HEALTHY；Vite5174/PID32812保留。

当前Web36files/391tests、typecheck、Service全量EXIT0、loss37/37、候选发布、loss HTTP18与双视口Chrome18、最新候选广覆盖浏览器PASS。SQLite/140Raw逐项长度SHA256、单DB检查点与19静态资源均通过。本批未重跑完整离线门禁，之前ICMP_PERMISSION_DENIED失败未解除；真实客户端/AP关联为UNKNOWN，HA/设备恢复/SCM/8–24h仍未验。

完整当前制品身份、证据路径、数据影响和续跑步骤见[R7_6_PING_LOSS_EVIDENCE_REPORT.md](R7_6_PING_LOSS_EVIDENCE_REPORT.md)。本节取代下文旧候选/PID/测试数作为当前值；下文为此前批次实际证据，未重跑的检查不得视为本批通过。

## 此前批次证据

日期：2026-11-08。Agent：`codex-A`。Branch：`codex-A/netconsole-next-r3-dual-ac`。本轮仅本地提交，未 push、remote merge 或 release publish。

总体状态：**PARTIAL**。

## 本轮交付

- Long Ping 完成暂停、恢复、取消、窗口截止、权限复核、历史持久化和 Quick/Long 独立趋势/CSV。暂停期间不补发样本，未发送样本不计丢包；P95 只使用实际回复。
- Windows 系统 ICMP Traceroute 保留真实启动、取消、撤权、输出上限和原生错误分类；不把 TCP 探测伪装成 ICMP。
- 无线工作台增加当前 AC 只读采集和 AP 有效变化 CSV；配置备份路径增加祖先重解析点保护；LocalManager 就绪诊断兼容非法 PID；故障中心取消任务不再制造失败故障。
- 浏览器验收脚本补齐真实 Long Ping 回环生命周期、双类型历史和 CSV 断言；默认 Chrome 路径修正为 Windows 可执行路径。

## 自动化与候选证据

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| Web | 35 files / 370 tests PASS | `pnpm test -- --reporter=dot` |
| Web 类型 | PASS | `pnpm typecheck` |
| Service 全量基线 | EXIT 0；Long Ping 3/3、权限 7/7、Quick Ping 9/9、故障 23/23、健康 17/17、配置恢复 18/18、Traceroute 15/15 等通过 | `reports/r76-service-full-final.log` |
| 最终候选 Long Ping HTTP | 13 项 PASS；真实 `127.0.0.1` ICMP、暂停/恢复/取消/窗口/P95/重启历史 | `reports/r76-long-ping-http-25e00169234f/result.json` |
| 最终候选权限 HTTP | 40 项 PASS；合成故障、Site/角色/CSRF/撤权范围 | `reports/r72-feature-http-20261108-220844-e695e3.json` |
| Long Ping 实际浏览器 | 1920x1080 与 2560x1440 全部 PASS；历史、CSV、Quick/Long 分组、P95、无新增探测 | `reports/r76-long-ping-browser-2026-11-08T14-09-52-034Z/result.json` |
| 共享候选只读浏览器 | 总体 PARTIAL；故障、当前 AC 导出、Ping、配置双分辨率 PASS；AP 历史因无有效变化记录保持 PARTIAL | `reports/r76-browser-final-20261108/result.json` |
| 共享候选广覆盖浏览器 | PASS；总览、故障、配置、无线导出均通过两个视口 | `reports/r76-broad-browser-final-20261108/result.json` |
| 候选离线门禁 | APPHOST 与 DOTNET_DLL_EXPLICIT 均 EXIT 1；唯一必需阻断为 `ICMP_PERMISSION_DENIED` | `reports/r76-offline-apphost.log`, `reports/r76-offline-dotnet.log` |

最终候选目录为 `D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63`，Service ProductVersion 为 `1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。候选构建早于本轮测试/报告提交；本轮后续提交只涉及验收脚本、报告和诊断证据，没有修改候选产品二进制。

## 数据与运行完整性

候选运行实例使用 `test-data\simulator-live-20261105`。`reports/r76-post-upgrade-integrity.json` 记录 SQLite `ok`、140 个 FINALIZED Raw 文件逐个长度和 SHA256 校验通过、INCOMPLETE/MISSING 为 0、recovery issues 为空；单数据库检查点 `test-data/r76-runtime-checkpoint-20261108/catalog.db` SHA256 为 `5fba203ba26a99caa2f9c05229646415f0e9bd1b64b048975f352e180098173f` 且完整性为 `ok`，19 个受保护静态资源未变化。未访问旧 NetConsole 数据根，未复制完整 Site 或数据根。

当前验收实例为 `http://127.0.0.1:18771/`，PID `49344`，健康状态 `HEALTHY`；Vite `http://127.0.0.1:5174/`，PID `32812`。运行候选使用上述 DLL 和候选 bundled Worker。

## 真实设备边界

AC1/AC2 已有产品 Netmiko 只读采集和 12 条 Raw/Facts 校验；SW 认证仍因无有效凭据阻断；FIT-AP 只到 Telnet Password 提示，未提交凭据；历史 `sys / probe / wlan ap-execute all exec-console enable` 被接受不足以证明当前 AP 生效。没有设备配置写入、FIT 原生登录、双 AC HA 切换、客户端连续性或物理恢复证据。

完整离线 Traceroute 在 APPHOST 和显式 DLL 两种门禁均因 `ICMP_PERMISSION_DENIED` 失败。独立 ICMP 对照证明环境差异不能解释全部结果，不能用窄诊断 PASS 替代完整 Host 门禁；详见 [R7_6_ICMP_ENVIRONMENT_REPORT.md](R7_6_ICMP_ENVIRONMENT_REPORT.md)。

远端设备、真实无线空口、配置 Writer/readback/rollback、Windows SCM、跨机安装和 24 小时长稳仍未完成，因此 R7.6 不升级为 RELEASE_READY。
