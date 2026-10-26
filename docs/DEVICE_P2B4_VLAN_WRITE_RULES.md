# P2B-4 VLAN 写入规则

VLAN ID 必须在 1–4094，tagging mode 必须符合 v13 的 ACCESS/TRUNK/HYBRID/QINQ CHECK。写入要求 domain 存在、ACTIVE 且 `scope_resolution=RESOLVED`。

同一 domain、同一 VLAN、有效期重叠的非 OBSERVED plan 返回 `VLAN_OVERLAP`。相邻区间允许；不同 domain 允许；OBSERVED plan 不参与占用判断，因此 planned 与 observed 可以共存。Observed 当前行不可被 PUT 覆盖。

PUT 要求 expected revision、保持 plan identity 和 source identity 不变，旧快照写入 `p2b4_vlan_history` 后 revision+1 更新。所有 direct write 和 import confirm 都是原子事务，不会覆盖 `vlan_assignments` 或其他 observed 数据。
