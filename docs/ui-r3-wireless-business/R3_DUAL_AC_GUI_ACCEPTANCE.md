# R3 双 AC 无线业务 GUI 验收

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
