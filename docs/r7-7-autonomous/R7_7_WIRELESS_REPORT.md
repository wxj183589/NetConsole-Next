# R7.7 无线产品与候选实采报告

## 最终候选结果（取代下文待补充状态）

状态：**PASS（SIMULATOR_READONLY）**，HA及客户端连续性NOT_TESTED。候选build/r77-candidate-api通过实际产品任务完成双AC各6条只读命令；证据reports/r72-product-dual-ac-20261108-233229/summary.json。12条命令完整、Raw长度/hash及Facts通过。

同一资源ap-resource:60d0aad0d9f1b381c501aff3；精确MAC4C:48:CB:6E:04:00、Serial H3C_4C-48-CB-6E-04-00，型号WA6320-HCL。AC1观察R/M，capturedMs1794151955536；AC2观察R/B，capturedMs1794151961380。各保留独立source/manifest，不以时间差推断切换，不把控制器Common改写成HA角色。

共享浏览器广覆盖Observation CSV两个视口PASS。有效变化历史为空，专项两项PARTIAL；没有人为制造状态变化。见UI报告。SQLite164 Raw完整，19原资产不变；设备写入0。

## 此前工作流记录

日期：2026-11-08。Agent：codex-A。设备验收子任务复用冻结的 R3 Domain / AC Member / AP Resource / AP Observation / ONE_PLUS_ONE 合同，不重新设计无线身份模型。

## 产品与既有证据

本轮设备相关产品改动为 Comware 完整提示符读取，详见 [设备报告](R7_7_DEVICE_INTEROP_REPORT.md)。无线当前 AC 快速采集、观察 CSV 和 Current / PreviousEffective / Recent10 导出已在 R7.6 实现，本轮不重复开发。源码回归、历史候选浏览器检查与新候选实际采集必须分开记录。

R7.6 双 AC 原件为 `D:\study\NetConsole-Next-Workspace\reports\r72-product-dual-ac-20261108-211219\summary.json`。资源 `ap-resource:60d0aad0d9f1b381c501aff3` 的精确 MAC `4C:48:CB:6E:04:00` 与序列号 `H3C_4C-48-CB-6E-04-00` 在两台 AC 各有一条独立观察；该旧快照不能计作本次新 Worker 的候选验收。

## 新候选验收范围

在主集成上线新候选后，通过产品既有 SSH profile 对 `192.168.56.2:22`、`192.168.56.5:22` 各采集六条固定只读命令：`display version`、`display wlan ac-role`、`display wlan ap all`、`display wlan ap all address`、`display wlan ap all radio`、`display wlan ap all verbose`。

验证每条任务命令 COMPLETE/PARSED、Raw segment/output 的长度与 SHA256，以及 Fact 的 source、scope、run 和 AP 观察 manifest；确认精确序列号/MAC 对应一个资源、两个 AC 来源不互相覆盖。另读 runtime-state，保留真实 freshness、采集结果和有效变化历史，不制造变化记录。

候选上线与本轮实采结果尚待补充。HA/回切/无线客户端连续性单独保持 [HA 报告](R7_7_HA_REPORT.md) 中的未完成状态；FIT 原生认证边界见 [FIT 报告](R7_7_FIT_AP_REPORT.md)。本子任务不修改配置，不重试 SW/FIT 已失败或未知凭据，不复制 Site、完整数据根或旧 NetConsole 数据。
