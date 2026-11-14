# NetConsole-Next 自主连续开发进度

更新时间：2026-11-15（Asia/Shanghai）

## 当前检查点

```text
TASK=NETCONSOLE_NEXT_AUTONOMOUS_CONTINUATION
BRANCH=codex-A/netconsole-next-r3-dual-ac
PRODUCT_BASE=84e53533e5197aa4075c53f98cf5fb0775189a05
PRODUCT_COMMIT=6a6239c79dcb67b7fe386d4ec9bf90610980bfc4
STATUS=PARTIAL
R3_PUSH=NO
REMOTE_MAIN_MERGE=NO
RELEASE=NO
```

本检查点先完成 R3.1 H3C 凭据权威收敛，再继续无线业务闭环。生产 DI 现在使用设备绑定的 Windows DPAPI vault；采集、终端和完整配置读取统一经 `IH3cCredentialAuthority`，profile 写入具备迁移门禁、失败脱敏审计和跨 DPAPI/Catalog 补偿。旧 Catalog 明文不会自动批量迁移，必须逐设备显式重录。此前 AP identity review/lifecycle 只读接口仍保留：页面随 AP 精确身份和投影修订读取当前 Site claims 可见的核验状态、生命周期、Raw/Observation 证据引用和阻断原因；迟到响应不会覆盖新的 AP 选择。

主要修改：

- `web/src/api/client.ts`：补充身份核验和生命周期响应类型及读取 API。
- `web/src/views/WirelessView.vue`：增加 AP 身份核验卡片、证据和阻断原因展示、生命周期状态映射及竞态保护。
- `web/src/views/WirelessView.test.ts`：增加候选身份、生命周期和精确证据展示回归。
- `src/NetConsole.Next.Service/H3cCredentialAuthority.cs`：增加 DPAPI authority、旧 Catalog 迁移门禁及写入补偿。
- `src/NetConsole.Next.Service/Program.cs`：H3C profile 写入统一走 authority，存储失败返回脱敏 503 并写入失败审计。
- `src/NetConsole.Next.Service/H3cSsh.cs`、`SimulatorConfigurationService.cs`：采集、终端和完整配置读取切换到统一 authority。
- `src/NetConsole.Next.Service/OperationalHealthService.cs`：将 sealed Syslog 无线 MESH 事件接入现有 OperationalHealth/FaultCenter 证据链，限定精确 AC、freshness、Raw 引用和脱敏详情。
- `tests/NetConsole.Next.Service.Tests/OperationalHealthTests.cs`：增加无线 Syslog 精确 AC 身份、freshness、peer MAC 和 severity 回归。
- `docs/autonomous-development/CREDENTIAL_AUTHORITY_REPORT.md`、`CREDENTIAL_MIGRATION_DECISION.md`、`R3_DPAPI_INTEGRATION_REPORT.md`：记录权威、迁移和集成边界。
- `docs/autonomous-development/AP_IDENTITY_LIFECYCLE_REPORT.md`、`DUAL_AC_OBSERVATION_REPORT.md`、`WIRELESS_FAULT_INTEGRATION_REPORT.md`：记录生命周期、双 AC 观察和故障证据闭环。

## 验证结果

```text
WEB_WIRELESS_VITEST=PASS (25/25)
WEB_TYPECHECK=PASS
WEB_FULL_VITEST=PASS (36 files / 451 tests)
WEB_BUILD=PASS (Vite + terminal bundle)
SERVICE_WIRELESS_TESTS=PASS (group contract; cache 4/4; replacement 3/3; identity contract 3/3; projection; runtime 14/14)
SERVICE_IDENTITY_LIFECYCLE_TESTS=PASS (5/5)
DPAPI_AUTHORITY_TESTS=PASS (explicit migration gate; device-bound key; Catalog redaction; provider resolution; revoke)
H3C_CONNECTION_POLICY_TESTS=PASS (8 groups)
SIMULATOR_CONFIGURATION_TESTS=PASS (28/28)
H3C_ONBOARD_SITE_TESTS=PASS
WIRELESS_SYSLOG_FAULT_CHAIN=PASS (OperationalHealth 27/27; FaultCenter 30/30; SyslogQuery 8/8; SyslogEvents 6/6)
WIRELESS_DUAL_AC_SCOPE=PASS (projection/runtime/identity/task/AC-resource/fact-scope contracts)
SERVICE_FULL_REGRESSION=PASS (default SchedulerTests; Release build 0 errors/0 warnings)
R3_FULL_REGRESSION_DOC=PASS (R3_FULL_REGRESSION.md)
RUNTIME_HANDOFF_DOC=PASS (RUNTIME_HANDOFF.md)
GIT_DIFF_CHECK=PASS
```

## R3.2 检查点

```text
TASK=NETCONSOLE_NEXT_R3_2_WINDOWS_H3C_WIRELESS
PHASE=R3.2
SCOPE=A+B+C
PRODUCT_COMMIT=6a6239c79dcb67b7fe386d4ec9bf90610980bfc4
STATUS=PARTIAL
R3_PUSH=NO
REMOTE_MAIN_MERGE=NO
RELEASE=NO
R3_2_A=PARTIAL_ADMIN_REQUIRED
R3_2_B=PARTIAL_AC1_AC2_ONLY
R3_2_C=PARTIAL_GLOBAL_MUTEX_BLOCKED
R3_2_D=OUT_OF_SCOPE_BY_USER
```

本检查点完成了 Windows 环境和 LocalManager/SCM 计划审计、AC1/AC2 与 SW/FIT-AP 只读互操作探测、无线 Syslog AP 精确关联和 link-up 故障恢复代码收口。真实管理员 SCM、SW/FIT-AP 完整认证、产品 Service HTTP 级 Syslog→Raw→OperationalHealth→FaultCenter→UI 链路仍保持真实状态，不用夹具替代。

本轮新增报告：`R3_2_A_WINDOWS_RUNTIME.md`、`R3_2_B_H3C_INTEROPERABILITY.md`、`R3_2_C_WIRELESS_FAULT_E2E.md`、`R3_2_FINAL_ACCEPTANCE.md`。服务级 E2E 隔离证据保存在 `reports/r3.2-c-e2e-20261115-000539`；H3C 只读证据保存在 `reports/r3.2-b-h3c-20261114-235450`。

服务端第一次并发启动两项 Release 定向测试时共享输出文件，出现一次编译文件占用；串行重跑后通过，不计为产品失败，但保留为环境执行注意事项。

## 设备凭据 PR 与 R3 边界

- Draft PR：[#49](https://github.com/wxj183589/NetConsole-Next/pull/49)
- Head：`codex-A/device-credential-dpapi-20261111` / `62fcd16895add61b9fb86d2d805cdc54ccd5539a`
- PR 状态：`OPEN + DRAFT`；当前无 Review、无 inline thread；GitHub Actions 两项已完成且均为 `success`。
- DPAPI 修复未整批移植到 R3：该分支以 `main` 的设备凭据工作流为基础，而 R3 使用现有 Catalog/DeviceRecord 凭据边界；直接合并会带入大量旧主线差异。R3 凭据权威迁移需另行定义 schema、迁移和真实数据门禁，本检查点不冒险扩大范围。

## 未关闭事项

- 真实设备、双 AC HA/failover/recovery、Windows SCM、8–24 小时运行、GUI 安装和原生 125%：`NOT_VERIFIED` 或 `PENDING`，不以离线夹具替代。
- AP identity 权威写入虽已有受隔离临时根保护的服务端合同和定向测试，生产 schema-backed authority、merge/split、replacement revoke 和跨进程并发仍未关闭。
- H3C 旧 Catalog 明文的真实数据迁移未执行；ZTE、Linux、Jump Host 仍是独立凭据存储边界，不能宣称全产品凭据已统一。
- 无线 Syslog 目前只验证 sealed 本地 Raw 与离线/代码合同；真实 AC MESH 事件、双 AC HA/failover/recovery、长时状态历史和真实 FaultCenter 恢复仍为 `NOT_VERIFIED`。
- 本轮前端构建生成的 `wwwroot/app/assets` 静态资源未纳入产品提交，既有未跟踪资源未修改、未清理；构建后的 `wwwroot/index.html` 已恢复到提交前引用，避免把构建副作用混入本批。
- `git fetch github` 本轮因网络连接被重置，`github/main` 未宣称已刷新；R3 不 Push、不合并远端 main、不发布。
- 主仓库既有 19 个未跟踪静态资源未修改、未暂存、未清理。

## 下一任务

执行 R3 服务端与前端全量回归并形成 `R3_FULL_REGRESSION.md`、`RUNTIME_HANDOFF.md`；随后继续评审 AP identity 权威写入的生产持久化边界。任何真实设备和生产数据动作仍需单独门禁。
