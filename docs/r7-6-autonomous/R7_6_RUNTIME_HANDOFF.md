# NetConsole-Next R7.6 Runtime Handoff

## 2026-11-08 最新集成检查点

本批已完成丢失采样历史上下文、实际Raw完整性复核并部署。产品56d16e3/6e48a0a，夹具与验收d99e103/8cd1e2f/0dd6a14。最新候选build/r76-loss-context-8cd1e2f，ProductVersion1.0.0+8cd1e2f96038acef6c63181ad49cb6d7c4920403，Service18771/PID41856 HEALTHY；Vite5174/PID32812保留。

当前Web36files/391tests、typecheck、Service全量EXIT0、loss37/37、候选发布、loss HTTP18与双视口Chrome18、最新候选广覆盖浏览器PASS。SQLite/140Raw逐项长度SHA256、单DB检查点与19静态资源均通过。本批未重跑完整离线门禁，之前ICMP_PERMISSION_DENIED失败未解除；真实客户端/AP关联为UNKNOWN，HA/设备恢复/SCM/8–24h仍未验。

完整当前制品身份、证据路径、数据影响和续跑步骤见[R7_6_PING_LOSS_EVIDENCE_REPORT.md](R7_6_PING_LOSS_EVIDENCE_REPORT.md)。本节取代下文旧候选/PID/测试数作为当前值；下文为此前批次实际证据，未重跑的检查不得视为本批通过。

## 此前批次证据

日期：2026-11-08。状态：**PARTIAL**。Branch：`codex-A/netconsole-next-r3-dual-ac`。本检查点基线 HEAD 为 `89705fd322f90cf889705704cb3432c0e1d5dcb1`，包括本地 R7.6 功能、验收脚本和事实报告提交；未 push、remote merge 或发布。后续历史丢包上下文批次正在独立 Worktree 开发，尚未部署。

## 当前运行实例

- Service：`http://127.0.0.1:18771/`
- PID：`49344`
- DLL：`D:\study\NetConsole-Next-Workspace\build\r76-candidate-integrated-91d0a63\NetConsole.Next.Service.dll`
- ProductVersion：`1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`
- Data root：`D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`
- Syslog：loopback UDP `15514`
- Vite：`http://127.0.0.1:5174/`，PID `32812`，由用户保留
- 健康：`HEALTHY`，SQLite `ok`，Raw `140 FINALIZED / 0 INCOMPLETE / 0 MISSING`，recovery issues `0`

启动使用 `tests/r70_runtime_control.py start --report-prefix r76 --web-port 18771 --syslog-port 15514 --data-root <上述 data root> --expected-dll <上述 DLL> --netmiko-worker <候选 bundled worker>`。停止必须使用同一脚本的 `stop --pid 49344 --expected-dll <上述 DLL>`，保持受控退出，不使用强制终止。

## 数据检查点

单一数据库检查点：`test-data/r76-runtime-checkpoint-20261108/catalog.db`。SHA256：`5fba203ba26a99caa2f9c05229646415f0e9bd1b64b048975f352e180098173f`，SQLite integrity `ok`。升级后核对报告为 `reports/r76-post-upgrade-integrity.json`，包含 140 个 Raw 文件逐个长度/SHA256 校验和 19 个受保护静态资源未变化。不要复制整个数据根，不要覆盖运行中的 `catalog.db`。

## 已验证范围

最终候选 Long Ping HTTP 与真实浏览器回环生命周期通过；功能权限 HTTP 40 项、广覆盖工作台浏览器通过；Web 35 files/370 tests 和 typecheck 通过；Service 全量基线 EXIT 0。离线 APPHOST 与显式 DLL 门禁仍因必需 Traceroute `ICMP_PERMISSION_DENIED` EXIT 1。300 秒无人值守报告 `reports/r73-unattended-16d8bd571b5b47b1bd41d46f6bf46bb1` 保留为短时证据，不是 24 小时保证。

## 下一交接批次

1. 在获准且可复现的 Windows 环境继续定位完整 Host 的 ICMP Traceroute 权限边界；保留当前真实失败分类。
2. 获取有效 SW/FIT-AP 凭据后先做只读身份与版本核对，再考虑任何配置操作。
3. 设计双 AC HA 切换前的恢复点和终端连续性测量；没有证据时维持 UNKNOWN/PARTIAL。
4. 补真实设备配置 Writer、回读、业务验证和失败回滚；补 elevated SCM 与独立主机验收。
5. 继续扩展轨交/Mesh 专业报表和丢包上下文，按采样时刻约束 Observation，缺失关联显示 UNKNOWN。

不得以回环、合成设备、离线计划或短时 soak 把上述边界升级为现场通过。
