# FD-P2-01 网络诊断批量任务

## 状态

`COMPLETED_LOCAL`：隔离 Catalog、Fake/本机网络 fixture、Service API、Tools 页面和重启后历史恢复已实现并通过定向回归。四台模拟器的 credential-backed 结果仍属于外部验收。

## 能力

- 目标只能是当前 Site claims 可见、已启用且库存中登记的设备 ID；不接受前端任意地址，避免把诊断接口变成任意网络探测器。
- 每个设备按所选类型拆分为独立子任务：`PING`、`TCP`、`DNS`、`LLDP`。
- 批次最多 128 台设备、512 个检查；批次内最多 4 路并发，单个失败不会影响其他设备。
- 每个子任务公开 `QUEUED/RUNNING/SUCCEEDED/FAILED/NO_EVIDENCE/CANCELLED`、耗时、粗粒度结果和进度计数；密码、凭据引用和异常堆栈不进入结果。
- 取消使用批次 CancellationToken，运行中和排队中的子任务均收敛到显式终态。
- 已完成批次以原子 JSON 写入 Next 数据根 `diagnostics` 目录，启动时恢复最多 128 条终态历史；未完成批次不伪造为已恢复。
- LLDP 不发送新命令，也不把空结果推断为拓扑；只读取带 `capturedScope` 的当前 H3C `lldp.neighbor` Facts，并返回来源与证据条数。
- UI 位于“工具集 → 网络诊断”，支持设备多选、类型选择、进度轮询、取消和明细查看。

## 接口

- `POST /api/system/diagnostics/runs`
- `GET /api/system/diagnostics/runs/{id}`
- `POST /api/system/diagnostics/runs/{id}/cancel`

诊断批次历史为有界持久化（128 批次）；读取时按当前 Site scope 重新过滤，文件损坏或越界记录会被忽略。

## 验证

- `NETWORK_DIAGNOSTICS_TESTS=4/4 PASS`
  - 两设备六检查的进度与失败隔离
  - 取消排队/运行中检查
  - 带 captured scope 的离线 LLDP Facts 证据
  - 服务对象重建后恢复已完成批次
- Service/Tests Release build：PASS。
- 完整 Service harness：PASS（将 `TEMP/TMP` 指到 Workspace 隔离目录；默认用户 Temp ACL 不可写）。
- Vue typecheck：PASS；Vitest `4 files / 7 tests PASS`；Vite build：PASS。

## 边界

- 没有执行四台模拟器的真实认证；现有安全凭据引用仍不可用，不能猜测、穷举或记录密码。
- 没有配置写入、HA 切换、实时 AP 归属或生产数据访问。
