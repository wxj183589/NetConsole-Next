# AP Group 候选缓存收敛验收

日期：2026-11-06。基线：`fda2a6807585f9c65f6f013950606a1fda0f8098`。Branch：`codex-A/netconsole-next-r3-dual-ac`。本批状态：`LOCAL_ACCEPTANCE_PASS`；功能提交为本文件所属 commit。整体产品仍 `PARTIAL`。不 Push、Merge、Release。

名称候选使用观察 revision 生成临时 locator，原先每次刷新都增加一行 `wireless_ap_groups`，50 次刷新可累积 50 个候选。该表为可重建的派生缓存，不是 API 的事实 Authority。

本批仅在既有 FULL 授权投影事务中淘汰有本轮实际来源证据、采集时间严格更早、且不在当前结果中的 null-identity 候选。保留 confirmed、未采集来源和同时间不同 revision；Raw、Facts、历史 Observation 不删除。来源时间水位读取真实 AP Observation 和逐来源 typed group evidence，不使用缓存写入时间冒充采集时间。旧快照不能重新插回已淘汰候选，也不能覆盖 confirmed 空组的较新水位。失败时全部随既有事务回滚。

这属于缓存收敛，不表示业务组 ENDED，不实现 AP replacement 或完整 Domain/group 生命周期；不新增 schema。

验证：Release build 0 warnings / 0 errors；`--wireless-tests` 的 Group 合同、Projection、Runtime 13/13 与新增缓存 4/4 均 PASS。四组缓存测试覆盖重复刷新、来源隔离、confirmed 保留、PARTIAL 拒绝、旧快照/候选归零、并发顺序、空组逐来源水位、同时间保留和发布失败回滚。`git diff --check` PASS。测试使用 Workspace 内隔离 SQLite，fixture 已清理。

主要文件：`WirelessProjectionPersistence.cs`、`WirelessGroupTests.cs`。没有访问旧 NetConsole 真实数据根，没有设备配置写入。真实设备与实时组证据仍 `REAL_DEVICE_PENDING`。
