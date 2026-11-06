# FD-P2-02 配置备份、版本历史与差异

## 2026-11-06 当前状态补充

本批功能已本地提交 `a3954a5`，文档以所属 commit 为准。配置备份继续复用既有任务/Raw/claims，设备 CLI 当前统一 Netmiko；普通密码存于受 ACL 保护的 Profile SQLite 字段，自动读取，不使用普通设备 DPAPI 凭据中心。

配置备份 1/1 隔离证据保持；当前 HCL AC1/AC2/SW 只读连接/命令 PASS 不代表配置备份真实业务已全部验收。Service harness、Vue typecheck、8 files/23 tests 和离线候选运行 PASS；真实设备、HA 与目标机安装仍待完成。没有设备配置写入。

## 以下为 2026-11-05 原批次历史记录

原批次的 DPAPI、凭据缺失与测试数量只作历史记录，当前连接模型以本节为准。

日期：2026-11-05

## 交付

- 配置备份复用现有 H3C BoundedCollectionScheduler、设备互斥、Raw seal、DPAPI credential reference、HostKey 和 Task Site scope。
- 支持批量选择 H3C 设备，最多 32 台，批内最多 4 路受控并行。
- 新增 API：
  - POST /api/system/config-backups
  - GET /api/system/config-backups/batches/{id}
  - GET /api/system/config-backups?profileId=...
  - GET /api/system/config-backups/{id}/download
  - GET /api/system/config-backups/{baseId}/diff/{targetId}
- 每个设备的备份保存为版本化文件和 JSON manifest，记录 Site、设备、采集时间、任务 run、大小和 SHA-256。
- 下载、历史和差异均重新执行当前 Site claims 校验；操作写入审计事件。
- 配置命令输出在进入 Raw 和备份文件前对 password、passphrase、pre-shared-key、community、private-key、secret 等敏感行脱敏，配置内容不发布到 H3C Facts。
- 新增“配置备份”页面，支持批量备份、历史查看、下载和版本差异。

## 验证

- CONFIG_BACKUP_TESTS=1/1 PASS
- H3C_TASK_FACT_PUBLICATION_TESTS=11/11 PASS
- NETWORK_DIAGNOSTICS_TESTS=4/4 PASS
- Service/Test Release build：PASS，0 warning / 0 error
- vue-tsc --noEmit：PASS
- Vitest：4 files / 7 tests PASS
- Vite build：PASS
- git diff --check：PASS
- 配置备份 manifest 路径穿越拒绝：PASS

## 边界

- 当前配置备份依赖设备已有安全凭据；四台模拟器凭据缺失时不宣称真实采集成功。
- 未执行真实模拟器登录、配置写入、切换、恢复或生产验收。
- 未修改正式 schema、真实数据根或旧 NetConsole 数据。
