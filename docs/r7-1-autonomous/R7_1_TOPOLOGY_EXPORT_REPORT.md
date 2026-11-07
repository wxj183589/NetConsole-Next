# R7.1 第三批：精确 LLDP 筛选与可追溯 CSV

日期：2026-11-08。状态：产品代码与定向验证完成；浏览器验收由根集成统一执行。本功能只读取已存在的授权 LLDP facts，不触发设备采集或配置写入。

## 缺口核验与实现

审查 Next `TopologyView.vue`，原先已有库存页、LLDP claims scope 和关系表，但没有设备 / 接口筛选和导出。原版配置导出提供历史差异与快照归档，`trackside_ap_business.py` 也保留 LLDP 接口、远端设备、地址等业务导出字段。本次只复用 Next 已有 `getH3cLldpClaims`，不重新实现采集、拓扑推断或导出服务。

- 来源设备按精确 profile ID 选择，选项同时显示 host；接口按精确完整字符串选择，`1/0/1` 不会混入 `1/0/10`；Site 按事实返回 ID 精确筛选。名称相同的邻居不合并，不用名称或 IP 猜测设备身份。
- “导出当前筛选 LLDP CSV”只包含当前表格匹配行，不重新请求 API。逐行保留 profile/local device/interface/neighbor port/chassis/management address/Site、捕获 UTC、source、fact revision、relation key、change kind、scope state 和 claims revision。
- 导出同时保留加载 UTC、导出 UTC、失败档案数组、达到读取上限的档案数组和 `CURRENT_FILTER_LOADED_CLAIMS_ONLY` 标记。文件使用 UTF-8 BOM、CRLF、全字段 CSV quoting；引号 / 逗号 / 多行字段保留，公式前缀 `= + - @`（包括前导空白）及 tab/newline 前缀做电子表格公式保护。
- 每档案仍最多读取 1000 条；达到上限明确提示可能不完整。页面和文件不会宣称完整网络快照，各行捕获时间可以不同。资产仍为现有服务端第一页，未将其当作全量导出。
- 刷新加入 generation 检查，旧响应和页面卸载后的响应不能覆盖新证据；刷新期间禁用导出，避免导出未完成加载的数据。

## 文件与验证

- `web/src/views/TopologyView.vue`：精确筛选、导出按钮、数据边界和过期响应防护。
- `web/src/api/topologyExport.ts`：独立纯 CSV 格式化，不修改共享 client。
- `web/src/views/TopologyView.test.ts`：7/7 PASS，覆盖已有加载 / 空 / 错 / partial，以及精确筛选、当前事实导出与过期刷新。
- `web/src/api/topologyExport.test.ts`：7/7 PASS，覆盖来源 / scope / partial 保留、CSV 转义和公式输入保护。
- `vue-tsc --noEmit`：PASS。

无后端 schema / 路由变更，无设备连接，无原版仓库修改，无旧数据根访问。最终静态构建、浏览器操作、Git 与 DevStatus 由根集成统一执行。

## 最终集成补录

完整最终harness EXIT0、真实SW三邻居产品重采、精确筛选及820bytes非空CSV浏览器验收已完成；双分辨率截图目视、0错误。前期空态和失败证据保留，最终范围见 [最终验收](R7_1_FINAL_ACCEPTANCE.md)、[LLDP实录修复](R7_1_LLDP_REAL_CAPTURE_REPAIR_REPORT.md) 和 [测试证据](R7_1_TEST_EVIDENCE.md)。
