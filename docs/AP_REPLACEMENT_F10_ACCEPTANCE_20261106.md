# AP replacement F10 隔离合同验收

日期：2026-11-06。基线：`2696390afb945efc457e39733b893d04c72a1932`。Branch：`codex-A/netconsole-next-r3-dual-ac`。本切片：`LOCAL_ACCEPTANCE_PASS`；整体产品仍为 `PARTIAL`。本批只增加测试和本记录，未发现需要修复的生产代码缺陷。

依据冻结合同：`R3_AC_AP_IDENTITY_AND_RELATION_MODEL.md:64–69` 要求更换使用新 AP resource 或明确 replacement relation，不覆盖旧历史；`R3_B0_TEST_SCENARIO_MATRIX.md` 的 SC-08/09/11/12 要求缺字段候选、冲突可见、不自动合并与来源独立。相关文档均位于 `docs/ui-r3-wireless-business/`。

新增 `WirelessReplacementTests`，由既有 `--wireless-tests` 和完整 harness 调用。测试通过真实隔离 SQLite、离线命令 facts、现有无线读模型、projection persistence 和 collection manifest detail 验证，不只对直接传入的运行态样本断言。

| 回归 | 已验证 |
|---|---|
| `ReplacementEvidenceCannotInheritPhysicalHistory` | serial/MAC 同时变化、仅 MAC 变化、仅 serial 变化均使用独立 opaque resource；重复名称、组和 IP 不合并。新 Current/PreviousEffective/Recent10 仅引用自身证据；不变刷新不增加变化。旧身份行、观察链接、旧来源详情和命令输出保留，重启结果一致。 |
| `IncompleteReplacementCannotBorrowAnOlderAddress` | 同轮缺 MAC 时保留新 CANDIDATE，不借旧地址，不把 raw I 投影为物理 offline；后续完整证据独立投影，缺字段历史明确 PARTIAL_EVIDENCE。 |
| `SimultaneousSerialOrMacConflictKeepsIndependentSources` | 双 AC 同 serial/异 MAC、异 serial/同 MAC 均保留两份来源和显式 CONFLICT，不附加任一物理 AP 的有效运行态历史。 |

各分支同时断言 source AC/Site、observation、revision、`fact:` 原始输出引用和过滤后的 collection 来源详情一致；撤销 Site claims 后详情和运行态不可见。旧观察没有被推断为 ENDED。这里验证的 Raw 边界是离线 command output 与 `fact:` provenance；未生成真实采集 Raw 分段，不据此声明实时 Raw 文件完整性或设备兼容。

验证：Release build `0 warnings / 0 errors`；`--wireless-tests` 退出码 0：`WIRELESS_REPLACEMENT_TESTS=3/3 PASS`、`WIRELESS_GROUP_CACHE_TESTS=4/4 PASS`、Group 合同 PASS、Projection PASS、`WIRELESS_RUNTIME_TESTS=13/13 PASS`；`git diff --check` PASS。fixture 全部位于 Workspace `test-data`，已清理。

仍待后续：正式 candidate 确认/拒绝、identity 合并/拆分、replacement link 的权威写入、撤销与 revision 并发处理合同；可靠的关系终止证据和完整 Domain/group 生命周期。本批未新增这些操作，也未按缺席推断 ENDED；没有 schema、生产数据或设备配置变更。真实设备仍为 `REAL_DEVICE_PENDING`。未执行 Push、Merge、Release。
