# R3 双 AC 无线业务 GUI 验收

## 2026-11-06 当前 AP Group UI 切片

当前状态：`LOCAL_ACCEPTANCE_PASS`。本切片展示 group scope、来源 AC、identity status、可空 source/identity key、source/membership revision、实际 observation IDs、evidence refs 与冲突原因；名称 only 明确为 AC candidate，不显示由名称伪造的稳定 key。

同名组按 opaque projection ID 选择详情；DOMAIN 展示依赖服务端明确 shared evidence 与确认 Domain/成员一致合同。AC/source 过滤后重新读取来源受限的 detail/members，不把同 AP 的其他 AC observation 带入当前组详情；迟到详情不能覆盖新选择。改名和无变化刷新不应造成 membershipRevision 变化。

核验入口为 `web/src/views/WirelessView.test.ts`、`tests/wireless_browser_e2e.mjs` 与 `tests/r3_dual_ac_api_e2e.py`。本机前端 9 files/29 tests、typecheck、独立 Vite PASS；Chrome 无线浏览器 fixture 15 状态 PASS（1920/2560、DPR1，API mocked，非 live）。真实 HTTP + 离线 fixture 的 API 脚本全部 16 个 marker PASS，包含 sourceAcId 过滤与 MAC-only 来源成员的 canonical 全局可解析链接；这不等同 live Service-backed 浏览器全链路。当前切片为 `LOCAL_ACCEPTANCE_PASS`；真实 HA、真实设备及 Windows native 125% 仍待独立证据。

下方旧 mutex/凭据阻塞与早期 GUI 数值仅作为历史，不代表当前环境。当前业务字段定义见 [AP Group 合同](R3_B0_AP_GROUP_CONTRACT.md)。

## 历史 GUI 实现与验收


## 已实现的导航合同

```text
无线业务
  ├── AC Domain
  │   ├── AC1
  │   │   ├── 总览
  │   │   ├── FIT-AP
  │   │   └── AP 组
  │   └── AC2
  │       ├── 总览
  │       ├── FIT-AP
  │       └── AP 组
```

页面数据来自无线只读 API，不创建 FIT-AP 设备库存。当前页面区分 Domain、成员 AC、物理 AP resource 和每个 AC 的 Observation；状态、身份、来源 AC、Site scope、AP Group 和证据待定状态均单独显示。

## 自动化检查

```text
vue-tsc=PASS
VITE_BUILD=PASS
WEB_VITEST=5/5 PASS
```

## 浏览器验收状态

```ini
DUAL_AC_GUI_TEST=BLOCKED
GUI_1920X1080=NOT_TESTED
GUI_2560X1440=NOT_TESTED
```

原因是当前已有预览进程持有 `Global\NetConsoleNext.Service`，新的隔离 Service 无法启动；同时真实模拟器登录尚未取得安全凭据。未使用旧预览页面冒充 R3 页面通过。

## 预期交互清单

- AC Domain 切换后只展示当前授权子集。
- AC1/AC2 成员分别展示稳定 Device ID、连接状态、角色和版本。
- 同一个 FIT-AP 在两个 AC 视图中可见，但物理 AP 只生成一个身份。
- AP 详情展示 AC1/AC2 独立 Observation、原始状态、规范化状态和来源 revision。
- AP Group 按 AC 作用域展示，同名组不跨 AC 合并。
- `UNKNOWN`、`STALE`、`CONFLICT`、`PARTIAL_SCOPE` 不统一渲染为离线。
