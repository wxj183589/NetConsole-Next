# NetConsole-Next R7.6 UI Acceptance

日期：2026-11-08。候选：`build/r76-candidate-integrated-91d0a63`，ProductVersion `1.0.0+7099775c7ded206d2b04d40faf4930e4d59c9297`。浏览器证据等级为 `SERVICE_E2E_BROWSER`，均使用 loopback Service，不连接真实设备，不写设备配置。

## 实际候选浏览器

`tests/r76_long_ping_browser.mjs` 使用实际 Service、Chrome 和最小 loopback inventory，在 1920x1080 与 2560x1440 均通过：

- Long Ping 实际 ICMP 回复、暂停排空、暂停期间无新增样本/丢失、恢复原任务、取消保留真实样本。
- 终态历史重新加载不触发探测；Quick Ping 与 Long Ping 按 `diagnostic_kind` 独立显示。
- CSV 下载逐项核对真实 HTTP 样本、回复/P95 和未发送不计丢包；无页面错误、水平溢出或越权 API 写入。

报告：`reports/r76-long-ping-browser-2026-11-08T14-09-52-034Z/result.json`。

## 共享候选只读工作台

`R76_UI_URL=http://127.0.0.1:18771 node tests/r76_browser_acceptance.mjs` 的两个视口结果在 `reports/r76-browser-final-20261108/result.json`：

- 故障筛选、当前 AC 选择/只读导出、Ping P95 历史 CSV、配置历史均 PASS。
- AP 状态历史导出操作成功，但没有存储的有效变化记录，两个视口均明确标记 `PARTIAL / NO_STORED_EFFECTIVE_CHANGES`；没有为了填充页面而启动采集或虚构变化。
- 页面错误为空、水平滚动未超过视口、阻断写请求为空；此共享数据脚本只提交登录请求，其余均为只读查询和导出。诊断生命周期操作由上述隔离回环脚本验证。

`tests/r73_browser_acceptance.mjs` 的广覆盖结果 `reports/r76-broad-browser-final-20261108/result.json`：总览图表、故障筛选、配置容量/批次、无线导出在 1920x1080 与 2560x1440 均 PASS，页面错误为空。

## 边界

此 UI 验收不证明 FIT-AP 登录、Traceroute 实际多跳结果、双 AC HA、设备 Writer 或跨机器安装。FIT 原生凭据缺失时页面保持阻断/未知状态；AP/Site/车辆关联没有可靠事实时保持 UNKNOWN。
