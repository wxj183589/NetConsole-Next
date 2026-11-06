# R3 无线业务实现报告

日期：2026-11-06。状态：**PARTIAL**。

已完成隔离验证：AC/AP projection、Observation、Current/Recent10、Site scope、schema v18 无线身份持久化、schema v19 collection manifest、AP Group 只读详情/成员/provenance 和跨 AC manifest read API/UI。稳定 identity 与唯一完整 MAC 部分证据关联保持冲突边界，不把不完整证据直接提升为已确认身份。

- `GET /api/system/wireless/ac-domains/{id}/collection-manifests`
- `GET /api/system/wireless/collection-manifests/{id}`
- 无线“采集记录”展示 source AC、source revision、Raw manifest reference 和当前可见 Observation。
- 列表/详情按当前 Site claims 过滤，source AC 过滤后的元数据由可见 item 重新聚合。

当前 Netmiko 已对 HCL AC1/AC2 通过自动认证及 AP 列表/地址/radio 只读命令；SW 版本/接口/LLDP 命令通过，FIT-AP 仍由 AC 管理。连接前置问题已解决，旧 DPAPI/Exec/独立 FIT-AP 凭据不再阻断本架构。

仍需正式 AP candidate/replacement、冲突裁决、Domain/group 完整生命周期，以及实时多 AC 数据进入业务模型、双 AC HA、切换/恢复和 AP 归属证据。当前命令返回不代表这些业务合同已闭环；真实硬件 `REAL_DEVICE_PENDING`。未写设备配置，未触碰旧真实数据。
