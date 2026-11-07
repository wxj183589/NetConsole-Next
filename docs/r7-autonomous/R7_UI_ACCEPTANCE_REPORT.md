# R7.0 运维工作台浏览器验收

日期：2026-11-07 开发，2026-11-08 最终验收（Asia/Shanghai）

状态：PASS_WITH_LIMITATIONS（已执行浏览器业务链）；全产品仍为 PARTIAL。设备详情首轮旧能力占位已实际修复并在最终部署读取到真实能力证据；FIT-AP 创建选项的非空集合检查也已独立复验通过。

## 验收方式

使用 `tests/r70_browser_acceptance.mjs` 启动真实 Chrome / Playwright，登录实际 Vue 工作台，通过认证 API 连接本轮授权模拟器。接口不使用 mock，不注入伪造健康数据，不制造设备宕机，不写入设备配置。

前端入口为 `http://127.0.0.1:5174`，后端为 `http://127.0.0.1:18771`。每个关键页面分别记录 1920×1080 与 2560×1440 截图，并检查 document 横向溢出。报告将记录运行时错误、API 错误和控制台错误，保留失败而不提升为成功。

## 已执行业务链

| 业务链 | 验证内容 | 状态 |
| --- | --- | --- |
| 登录 | 实际超级管理员登录并进入运行总览 | PASS |
| Quick Ping | 工具集选择明确设备，执行 TCP 三次真实采样，显示每次样本 | PASS，3/3 成功，平均 0.67ms，0% 丢失 |
| 设备健康 | 当前授权库存、UNKNOWN 指标、Quick Ping 投影、证据边界 | PASS，6 台授权库存，缺少证据/过期显示 UNKNOWN |
| 受控只读恢复 | 明确选择目标，连接探测，读取补采计划中版本/接口命令及 Raw / Parse 结果 | PASS，一次 CONNECTED，两命令 SUCCEEDED / COMPLETE / PARSED |
| 设备详情 | 实际能力信息；FIT-AP 不出现独立创建选项 | PASS：旧占位已移除，身份/接口 VERIFIED；创建选项非空且不含 FIT_AP |
| 完整配置 | 读取已验证完整备份、脱敏预览、两个历史备份比较 | PASS，5 份完整备份，比较结果原始配置一致 |
| 无线业务 | 实际无线页面与任务历史状态 | PASS，加载完成后显示 2 个 Domain、1 个可见 FIT-AP 与 9 条任务 |
| 拓扑资产 | 实际资产、LLDP 来源和时间信息 | PASS，6 台库存，LLDP 为空时明确空态 |

Quick Ping、设备健康、只读恢复、设备详情、完整配置、无线、拓扑均已完成两个分辨率截图与 document 横向溢出检查，共 14 项宽度检查全部通过。浏览器实际图像复核了恢复结果、设备能力详情、完整配置、无线完成态和拓扑页面，没有页面整体横向溢出。无线页明确显示拓扑 EVIDENCE_PENDING、HA 切换 NOT_TESTED，没有把 R/M 与 R/B 观察提升为控制器切换验证。

## 证据路径

初轮：`D:\study\NetConsole-Next-Workspace\reports\r70-browser-20261107\result.json` 与同目录截图。该轮 B 链与完整配置 PASS，设备详情旧占位 FAIL；无线步骤因脚本匹配多个 `<main>` 而失败，已精确改为 `.app-main`。

无线/拓扑复验：`D:\study\NetConsole-Next-Workspace\reports\r70-browser-wireless-topology-20261107\result.json` 与同目录两分辨率截图。脚本追加了真实 Domain/AP 表格和 loading mask 消失等待，实际加载完成态 PASS；API / page / console 错误均为 0。

最终完整运行：`D:\study\NetConsole-Next-Workspace\reports\r70-browser-final-20261108\result.json`。本地时间 2026-11-08 00:00:55–00:01:12，Service PID 44424，7/7 业务步骤 PASS，API failures / pageErrors / consoleErrors 均为 0。设备详情从真实补采 Run `845c17ba17db467ab1aafcd8f7696df8` 展示身份和接口能力 VERIFIED / RECENT；无证据的 VLAN、LLDP、CPU、内存、HA、整机恢复保持 UNKNOWN。

最终运行后审查发现高级设备资料 select 的原 label locator 获取到空 option 数组，因此补充“选项必须非空”和精确 label 内 span 定位；不将原空数组断言作为 FIT-AP 创建限制验证。根 Agent 完成服务生命周期 Gate 后，使用新 PID 83920 实际执行设备专项复验：`D:\study\NetConsole-Next-Workspace\reports\r70-browser-device-final-20261108\result.json`。登录、能力详情、两个分辨率截图与创建选项检查均 PASS，API / 页面 / 控制台错误均为 0；选项包含 AC、FAT_AP、H3C_TERMINAL、NETWORK_DEVICE、ROUTER、SWITCH，非空且不含 FIT_AP。

脚本退出前等待未完成的 route 安全撤销，避免测试浏览器关闭产生无关的 Request context disposed；传输失败写入报告并中止该请求，保留实际失败证据。

## 限制

正常模拟器上的只读探测和补采成功不代表真实宕机恢复。该脚本不执行整机配置恢复、不人为断开 AC、不宣称关键业务恢复或 FIT-AP 原生 CLI 支持；这些能力需引用相应专项工作流的独立证据。

当前全产品状态仍为 PARTIAL；不得以局部页面通过替代整机恢复、双 AC 1+1、服务验证和回滚的业务闭环。
