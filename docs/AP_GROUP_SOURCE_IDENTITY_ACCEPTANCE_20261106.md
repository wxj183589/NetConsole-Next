# AP Group 来源身份修复与验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。起点：`8885705`。当前状态：`LOCAL_ACCEPTANCE_PASS`，整体仍为 `PARTIAL`。功能提交以本文件所属 commit 为准。仅本地提交，不 Push、Merge、Release。

## 问题与实现边界

旧投影将 AP 列表中的 `Group` 展示文本写成 `sourceGroupKey`，无法区分名称和厂商稳定键。详情还可能从同一物理 AP 带回另一 AC、另一组的观察记录。

当前补充独立的 `WirelessSourceGroupEvidence`，包含来源 AC/Site、厂商键、名称、观察 ID、采集时间、来源 revision、Raw/parser 引用及可选 Domain 共享证据。只有明确厂商键与完整来源证据才确认 `AC|ac_device_id|source_group_key`；Domain 身份另需明确共享证据和当前成员关系一致。当前 H3C AP parser 仍只提供名称，不伪造厂商键。

只有名称时保留 `AC/CANDIDATE`，`sourceGroupKey` 和 `groupIdentityKey` 为 null。候选投影 ID 只用于本次来源观察的定位，不提供稳定身份承诺。跨 AC 同名组独立；同 scope 同名不同键返回冲突。同键改名保留身份；成员版本依据身份、来源及成员关系计算，不随名称或无变化刷新而改变。

列表、详情、成员支持 `sourceAcId`，按当前 Site claims 和来源筛选后计算计数、成员摘要、来源引用与版本。仅用当前已授权来源建立物理 AP 的 canonical locator；AC 筛选保留该 locator，同时移除其他来源的成员字段与状态。未知或不可见组返回一致 404。

## 存储

schema23 保持 Linux 终端迁移用途。schema24 只更新可重建的 `wireless_ap_groups` 投影表：允许空来源键，保存身份状态、成员版本和来源证据；旧名称键清空并降为候选，保留名称和旧成员记录。迁移在单事务中执行，重复初始化不重复迁移，失败回滚。

这不是完整 AP candidate/replacement、Domain/group 生命周期或 Current/Recent10 的交付，不增加 AC 配置写回。

## 验证记录

- 前端：Vitest `9 files / 29 tests PASS`；类型检查 PASS；独立 Vite 生产构建 PASS，输出在 Workspace `build/ap-group-ui-20261106`。保留现有 bundle 大小提示。
- 后端：AP Group 合同 10 个场景方法、Projection、Runtime 13/13 PASS；schema24 全新初始化、旧名称键降级、重开、幂等与失败回滚 PASS。
- HTTP：`py -3.13 -B tests/r3_dual_ac_api_e2e.py`，16 个 PASS marker。实际启动隔离 Service，覆盖 AC/Domain 明确证据、改名/冲突、来源/Site 权限与 MAC-only 成员链接可解析；数据源为离线 fixture。
- Service：`dotnet run --project tests/NetConsole.Next.Service.Tests -c Release --no-build` 完整 harness PASS，包含凭据/Linux/Jump、调度/Raw/权限、迁移及 SQLite 回归。
- 浏览器：`node tests/wireless_browser_e2e.mjs`，15 状态 PASS（无线 11、全局文件 4），1920×1080 与 2560×1440、DPR1；最终中文状态说明与字体调整后复验 PASS。API 使用 backend-generated fixture/mock，证据位于 Workspace `build/autonomous-20261105/gui`。
- UI 与 HTTP 使用脱敏 fixture；不作为 HCL 实时 AP Group、HA 或真实设备证据。

## 与连接批次的关系

Netmiko/Linux/Jump 功能提交为 `a3954a5`，文档提交为 `8885705`。已验收离线候选 `build/service-web-linux-jump-a3954a5-20261106` 的 ProductVersion 为 `1.0.0+a3954a5e95590782a576272d4e4b3c526eab248b`，对应连接批次，不包含本次 AP Group 修复。其内置 Python/Netmiko、静态 UI、网络连接和 Linux WebSocket 五项门禁通过；目标机安装、SCM、跨机环境仍待验收。

本次仅使用 Next Workspace 隔离测试。没有访问旧 NetConsole 真实数据根，没有远端配置写入。真实设备、真实 Linux/堡垒机、双 AC HA/切换/恢复及实时 AP 归属继续标记 `REAL_DEVICE_PENDING`。
