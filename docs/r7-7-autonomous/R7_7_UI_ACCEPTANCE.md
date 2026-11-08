# R7.7 UI验收

候选 build/r77-candidate-api；真实Chrome、实际HTTP，不mock业务接口。1920×1080 / 2560×1440；Windows125%不在范围。

| 检查 | 结果 / 证据 |
| --- | --- |
| 配置变化真实差异/精确版本/导出/清空旧差异/无额外采集 | FIXTURE_SERVICE_BROWSER 16/16 PASS；r77-drift-browser-2026-11-08T15-31-49-266Z/result.json |
| 仪表盘模块图表、故障筛选、配置容量/历史、无线Observation导出 | SERVICE_E2E_BROWSER 4项PASS、8个视口截图无溢出；r77-final-broad-browser-20261108/result.json |
| 故障筛选、当前AC导出、AP历史、Ping P95历史、配置批次 | SERVICE_E2E_BROWSER 10项：8 PASS、2 PARTIAL；r77-final-browser-20261108/result.json |
| AP有效变化历史 | 两视口PARTIAL：NO_STORED_EFFECTIVE_CHANGES；导出读取成功但无变化事件可核验，不制造历史 |
| 完成Long Ping历史 | 当前共享数据无已完成Long Ping点；页面CSV验证使用已有Quick Ping，不能宣称真实Long Ping历史验收 |
| Web单元/类型 | 36files393tests、typecheck PASS；r77-web-full.log、r77-web-typecheck.log |

上述共享实例检查设备连接/配置写入0，无页面错误；专项脚本因PARTIAL退出1，不能写成整体全绿。截图留在各证据目录，不提交私有数据或大型图片。此前loss专项浏览器18项已通过（cfc801c候选），最终候选改动API404后配置专项与共享广覆盖复验通过。
