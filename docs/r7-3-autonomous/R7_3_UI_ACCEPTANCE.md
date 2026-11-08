# R7.3 UI与构建验收

2026-11-08，codex-A。前两批实现和真实静态Web浏览器验收 **PASS**，整体产品与设备验收保持专项状态。

## 产品改动

- 配置页显示原件清单数量、当前设备占用、共享额度、磁盘余量和明确留存策略。按需读取容量，不触发设备连接。
- 同页可查询最近可读完整备份批次，查看已有逐设备结果并沿用仅失败设备重试，不再依赖某个计划的最近执行链接。
- 无线页当前筛选直接导出Observation CSV，保留独立AC和Site/Domain/Raw来源。刷新失败清除旧AP资源。
- 故障中心新增LLDP邻居消失筛选、捕获分区详情和CSV来源字段。
- 延用现有SmartHub风格clean-room界面与路由，没有新增独立业务页面或动画。

## 构建结果

现有路由已动态加载。此次仅将全量ElementPlus安装改为项目实际使用的ElAlert、ElButton、ElDialog、ElIcon、ElLoading、ElPagination，并将Dashboard的全量ECharts改为Pie、Legend、Tooltip、CanvasRenderer。现有v-loading和程序化消息保持。

| JS包 | R7.2 | R7.3首批最终构建 |
| --- | --- | --- |
| index | 1034.54kB | 354.54kB |
| Dashboard | 1158.81kB | 476.80kB |
| Terminal | 332.11kB | 332.11kB |

Vite构建成功，未提高chunkSizeWarningLimit，未增加手动依赖分包，当前没有超过500kB的JS包警告。完整CSS继续保留。证据为Workspace `reports/r73-initial/web-build-integrated.log`。

## 真实浏览器

Chromium访问实际交互Service18771提供的静态构建Web，使用现有隔离模拟器数据根。没有mock API、设备连接或写配置操作。

- 既有10场景全部通过，覆盖故障/配置预览与历史/趋势/设备LLDP/拓扑/Syslog和导出。证据 `reports/r73-browser-regression/result.json`，API、页面及控制台错误均0。
- 新增4场景通过，覆盖模块化图表真实canvas、LLDP故障筛选、实际容量API和批次历史详情、按Domain导出独立AC观察。证据 `reports/r73-browser-product-fixed/result.json`。
- 每个场景保留1920×1080与2560×1440截图，文档无横向溢出。人工查看配置页截图，主要表格、容量与历史控件均可见。
- 本次容量样本为AC1的8份manifest、79298bytes，当前共享316388bytes。读取为本机存储证据，不能说明设备本次连接成功。

首轮新增无线测试错误地要求默认单Domain导出包含两个不同Domain。实际过滤按Domain精确保留正确，测试改为逐Domain操作并核验两个AC各自可导出，保留精确Domain约束。原始失败证据保留在 `reports/r73-browser-product/result.json`。单元测试另覆盖同一个Domain含两个独立AC时的两行观察保留。

Windows125%不在本轮范围。实际模拟器互操作、HA业务、SCM与长稳没有从UI结果提升状态。
