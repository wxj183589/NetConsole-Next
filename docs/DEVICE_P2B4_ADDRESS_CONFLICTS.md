# P2B-4 地址冲突规则

地址 claim 的冲突键不是全局 IP，也不是 CIDR 网络，而是：

`network_domain_id + address_family + normalized_address + overlapping validity + other device`

validity 使用半开区间 `[valid_from, valid_to)`；NULL 表示无界。prefix length 不参与冲突消除，因此同一 domain 中 `10.0.0.1/24` 和 `10.0.0.1/32` 仍冲突。不同 domain 可以复用地址，同一 device 的不同 source fact 可以保留。

创建冲突返回 `ADDRESS_ALREADY_CLAIMED`，不会插入 address 或 claim。更新要求 `expectedRevision`，旧 revision 返回 `STALE_REVISION`；Observed 行返回 `OBSERVED_PROTECTED`。更新会先将旧快照写入 `p2b4_address_history`，再以 revision+1 原子更新当前行，保留来源轨迹。

P2B-4 从不把地址写回 legacy `device_inventory.management_ip`，也不删除 P2B-2 backfill 或 observed evidence。
