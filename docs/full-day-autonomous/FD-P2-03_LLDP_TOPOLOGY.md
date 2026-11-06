# FD-P2-03 LLDP 关系与变化历史

## 2026-11-06 当前状态补充

本批功能已本地提交 `a3954a5`，文档以所属 commit 为准。当前 SW 经 Netmiko 的版本、接口摘要和 LLDP 只读查询 PASS；证据见 Workspace `reports/netmiko-simulator-20261106.json`。这证明命令返回，不自动证明完整拓扑解析/业务采集的真实设备验收。

LLDP 2/2 隔离关系/历史证据保持，当前 Service harness、Vue typecheck、8 files/23 tests PASS。HA、实时 AP 归属与实际硬件仍 `REAL_DEVICE_PENDING`，未写配置。

## 以下为 2026-11-05 原批次历史记录

下文“未执行真实模拟器登录”及测试数量仅描述原批次。

日期：2026-11-05

## 交付

- 从带 capturedScope 的 H3C lldp.neighbor Facts 投影本地设备、接口、邻居设备、邻居端口、管理地址、Site、来源、采集时间和 Fact revision。
- 关系历史按稳定关系键区分 FIRST_SEEN、REPEAT 和 CHANGED；缺失事实不会生成 REMOVED。
- 新增只读 API：
  - GET /api/system/h3c/profiles/{id}/lldp-relations
  - GET /api/system/h3c/profiles/{id}/lldp-claims
- API 复用当前 Site claims 和 captured scope 约束；无授权返回 403，越权 profile 不返回关系。
- H3C 事实页显示关系历史和当前 claims revision。

## 验证

- H3C_LLDP_TOPOLOGY_TESTS=2/2 PASS
- H3C_FACT_SCOPE_TESTS=8/8 PASS
- NETWORK_DIAGNOSTICS_TESTS=3/3 PASS
- Service Release build：PASS
- vue-tsc --noEmit：PASS（直接调用本地 node_modules/.bin/vue-tsc.cmd；pnpm wrapper 因 ignored build scripts 阻断）
- git diff --check：PASS

## 边界

- 只读取已验证 Facts；没有 captured scope 的 LLDP 记录不会进入关系投影。
- 未执行真实模拟器登录、双 AC HA、切换、恢复或配置写入。
- 没有修改真实数据或正式数据库 schema。
