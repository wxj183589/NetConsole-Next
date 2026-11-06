# R3 无线业务实现报告

## 2026-11-07 AC资源增量与无线边界：ef57213

日期：2026-11-07。Branch：`codex-A/netconsole-next-r3-dual-ac`。功能提交 `e93b38280328beb96bbbb1b172b3d0a01179cfef`；备份下载 no-store 修复及当前产品 HEAD `ef5721354fc56004f018a2b851b1aee9fd8b0bdd`。整体 **PARTIAL / REAL_DEVICE_PENDING**。保留当前分支，不 Push、Merge、Release。

证据提交范围：完整后端 session23456、Worker40/40、前端14files/140tests及typecheck通过于 `e93b382`；`ef57213` no-store修复后重跑Service Release构建、配置HTTP28与完整离线十项，未重复完整harness/Worker/前端单测。

AC必需3/可选6/补充概览3固定命令接入sealed Raw/命令Facts及当前历史UI，version/device沿用已有投影；FIT-AP仍经AC管理。backend3/3、HTTP21、Chrome7与当前离线AC_RESOURCES PASS。

真实采集catalog仅组名观察，未取得稳定生产GroupEvidence。两个AC同名组分别为CANDIDATE，SourceGroupKey/GroupIdentityKey为null，名称/APID/hash不当厂商key。已有显式来源fixture与schema24投影不代表现场已具备稳定组身份。

candidate确认/拒绝、identity合并拆分、replacement写入/撤销/revision、可靠终止及Domain/group生命周期仍待；F10隔离/缓存收敛不替代，不从缺席推断ENDED。HA/切换恢复/实时归属无现场证据，保持PARTIAL。

专项：[AC 资源](../H3C_AC_RESOURCES_ACCEPTANCE_20261106.md)、[三类备份](../CONFIG_BACKUP_KINDS_ACCEPTANCE_20261106.md)、[全机诊断](../H3C_FULL_DIAGNOSTIC_ACCEPTANCE_20261106.md)。最新结构化报告位置：`D:\study\NetConsole-Next-Workspace\reports\netmiko-final-ef57213-20261107.json`。当前总入口为 [运行状态](AUTONOMOUS_RUN_STATE.json)、[测试矩阵](COMPLETE_TEST_MATRIX.md)、[最终交接](FINAL_HANDOVER.md)。


## 2026-11-06 及更早记录（历史）

以下旧检查点、任务状态、测试数量、候选路径及“当前/最终”均保留当时证据范围；最新结果和未完成项以上方 2026-11-07 章节为准。

日期：2026-11-06。状态：**PARTIAL**。

已完成隔离验证：AC/AP projection、Observation、Current/Recent10、Site scope、schema v18 无线身份持久化、schema v19 collection manifest、AP Group 只读详情/成员/provenance 和跨 AC manifest read API/UI。稳定 identity 与唯一完整 MAC 部分证据关联保持冲突边界，不把不完整证据直接提升为已确认身份。

- `GET /api/system/wireless/ac-domains/{id}/collection-manifests`
- `GET /api/system/wireless/collection-manifests/{id}`
- 无线“采集记录”展示 source AC、source revision、Raw manifest reference 和当前可见 Observation。
- 列表/详情按当前 Site claims 过滤，source AC 过滤后的元数据由可见 item 重新聚合。

当前 Netmiko 已对 HCL AC1/AC2 通过自动认证及 AP 列表/地址/radio 只读命令；SW 版本/接口/LLDP 命令通过，FIT-AP 仍由 AC 管理。连接前置问题已解决，旧 DPAPI/Exec/独立 FIT-AP 凭据不再阻断本架构。

仍需正式 AP candidate/replacement、冲突裁决、Domain/group 完整生命周期，以及实时多 AC 数据进入业务模型、双 AC HA、切换/恢复和 AP 归属证据。当前命令返回不代表这些业务合同已闭环；真实硬件 `REAL_DEVICE_PENDING`。未写设备配置，未触碰旧真实数据。
