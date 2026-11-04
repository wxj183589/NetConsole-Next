# R2 Windows 原生 125% 最终复核

日期：2026-11-04

结论：`WINDOWS_NATIVE_125PCT=PENDING_ENVIRONMENT_NOT_READY`

## 1. 环境裁决

本轮重新检查了 Windows 注册表、Win32 进程/窗口 DPI、显示器有效 DPI，以及隔离 Chrome 页面指标。注册表中的 `LogPixels=120` 不足以证明当前桌面已经以 125% 有效运行；实际 Win32/Chrome 窗口 DPI 仍为 96。

| 指标 | 本轮实际值 | 结论 |
| --- | --- | --- |
| `WINDOWS_EFFECTIVE_SCALE` | `100%` | 未达到原生 125% |
| `WINDOWS_EFFECTIVE_DPI` | `96` | Win32 当前有效 DPI |
| `CHROME_DPI` | `96` | 当前可见 Chrome 窗口/显示器有效 DPI |
| `REGISTRY_LOGPIXELS` | `120` | 仅为注册表值，不作验收依据 |
| `CHROME_ZOOM` | `100%`，隔离页面 `visualViewport.scale=1` | 未使用页面缩放模拟 |
| `BROWSER_DPR` | `1` | 非原生 125% |
| `CSS_VIEWPORT` | `1584×849` | 隔离 headed Chrome，`viewport=null` |
| `SCREENSHOT_PIXEL_SIZE` | `1584×849` | `chrome-current-environment-probe.png` |
| `FONT_ENVIRONMENT` | `Segoe UI, Arial, sans-serif; 16px` | 仅记录环境，不推导 125% |

Win32 探针还观测到可见 Chrome 窗口 DPI 为 96、显示器有效 DPI 为 96；隔离 Chrome 探针输出保存在 Workspace 临时证据目录：

```text
D:\study\NetConsole-Next-Workspace\temp\r2-native-125-recheck-20261104\chrome-current-environment-probe.json
D:\study\NetConsole-Next-Workspace\temp\r2-native-125-recheck-20261104\chrome-current-environment-probe.png
```

本轮没有修改 Windows 缩放、分辨率、DPI 兼容性、用户 Chrome Profile，也没有注销、重启或结束用户 Chrome。

## 2. GUI 证据边界

由于有效环境仍为 100%，本轮没有生成任何 `*-125pct-native.png`，也没有把 CSS viewport、`deviceScaleFactor` 或页面缩放当作原生 125% 证据。隔离 Service Browser CRUD 生成的四张截图只作为当前 100% 回归基线：

```text
D:\study\NetConsole-Next-Workspace\temp\r2-native-125-recheck-20261104\current-100pct-browser-evidence\
```

这些截图覆盖新增、详情/审计、批量操作和 CSV 导入，结果均为 `PASS`；它们不关闭原生 125% 门禁。

## 3. 原生 125% 裁决

```text
WINDOWS_NATIVE_125PCT=PENDING_ENVIRONMENT_NOT_READY
NATIVE_125PCT_SCREENSHOTS=0
NATIVE_125PCT_VISUAL_DEFECTS=0
NATIVE_125PCT_DEFECTS_FIXED=0
GATE_D_GUI=PARTIAL
```

这里的 `0` 表示没有在原生 125% 场景中完成截图或缺陷复验，不表示 125% 场景已经无缺陷。R2 只能在用户将 Windows 切换到真实有效 125% 后重新执行设备列表、Site、分组、筛选分页、新建、编辑、详情、权限/空态/加载态和窄视口验收。

