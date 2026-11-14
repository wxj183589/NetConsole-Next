# NetConsole-Next 自主连续开发进度

更新时间：2026-11-14（Asia/Shanghai）

## 当前检查点

```text
TASK=NETCONSOLE_NEXT_AUTONOMOUS_CONTINUATION
BRANCH=codex-A/netconsole-next-r3-dual-ac
PRODUCT_BASE=84e53533e5197aa4075c53f98cf5fb0775189a05
PRODUCT_COMMIT=a9027d86281bf1a9da250becd866a431805cf134
STATUS=PARTIAL
R3_PUSH=NO
REMOTE_MAIN_MERGE=NO
RELEASE=NO
```

本检查点把现有 AP identity review/lifecycle 只读接口接入无线工作台。页面随 AP 精确身份和投影修订读取当前 Site claims 可见的核验状态、生命周期、Raw/Observation 证据引用和阻断原因；迟到响应不会覆盖新的 AP 选择。页面不自动确认、替换或推断离线，也不扩大 Site 范围。

主要修改：

- `web/src/api/client.ts`：补充身份核验和生命周期响应类型及读取 API。
- `web/src/views/WirelessView.vue`：增加 AP 身份核验卡片、证据和阻断原因展示、生命周期状态映射及竞态保护。
- `web/src/views/WirelessView.test.ts`：增加候选身份、生命周期和精确证据展示回归。

## 验证结果

```text
WEB_WIRELESS_VITEST=PASS (25/25)
WEB_TYPECHECK=PASS
SERVICE_WIRELESS_TESTS=PASS (group contract; cache 4/4; replacement 3/3; identity contract 3/3; projection; runtime 14/14)
SERVICE_IDENTITY_LIFECYCLE_TESTS=PASS (5/5)
GIT_DIFF_CHECK=PASS
```

服务端第一次并发启动两项 Release 定向测试时共享输出文件，出现一次编译文件占用；串行重跑后通过，不计为产品失败，但保留为环境执行注意事项。

## 设备凭据 PR 与 R3 边界

- Draft PR：[#49](https://github.com/wxj183589/NetConsole-Next/pull/49)
- Head：`codex-A/device-credential-dpapi-20261111` / `62fcd16895add61b9fb86d2d805cdc54ccd5539a`
- PR 状态：`OPEN + DRAFT`；当前无 Review、无 inline thread；GitHub Actions 两项已完成且均为 `success`。
- DPAPI 修复未整批移植到 R3：该分支以 `main` 的设备凭据工作流为基础，而 R3 使用现有 Catalog/DeviceRecord 凭据边界；直接合并会带入大量旧主线差异。R3 凭据权威迁移需另行定义 schema、迁移和真实数据门禁，本检查点不冒险扩大范围。

## 未关闭事项

- 真实设备、双 AC HA/failover/recovery、Windows SCM、8–24 小时运行、GUI 安装和原生 125%：`NOT_VERIFIED` 或 `PENDING`，不以离线夹具替代。
- AP identity 权威写入虽已有受隔离临时根保护的服务端合同和定向测试，生产 schema-backed authority、merge/split、replacement revoke 和跨进程并发仍未关闭。
- `git fetch github` 本轮因网络连接被重置，`github/main` 未宣称已刷新；R3 不 Push、不合并远端 main、不发布。
- 主仓库既有 19 个未跟踪静态资源未修改、未暂存、未清理。

## 下一任务

先观察 Draft PR #49 的 CI 结果并处理明确失败；随后在不改变 R3 无线事实边界的前提下，继续评审 AP identity 权威写入的生产持久化边界，或选择可由现有证据直接验证的无线业务缺口。任何真实设备和生产数据动作仍需单独门禁。
