# R7.7 最终集成验收与检查点

日期：2026-11-08；Agent：codex-A；会话：01a1ba51-04f2-71a2-ac64-17bb40048093。
分支：codex-A/netconsole-next-r3-dual-ac；基线 b4d942e；仅本地提交，未 Push、远端合并或发布。总体状态：**PARTIAL**。

## 已交付产品批次

| 提交 | 产品行为 |
| --- | --- |
| 2ad4698 | Comware SSH/Telnet 读取完整终止提示符，避免正文设备名造成截断 |
| dd72333 | 完整配置最近两个有效版本变化检测、UNKNOWN 原因及差异 UI |
| b9d27de | 连续三个有效丢包确认故障、同资源三个新回复证明恢复 |
| cfc801c | 同进程原生 ICMP 部署诊断，保留生产 Traceroute 独立结果 |
| 1cd8996 | 不存在/未启用 API 返回 JSON 404，不再落入 Vue HTML |
| bb84c9f | 配置变化HTTP/浏览器验收、运行完整性与有界健康观察工具；不包含设备凭据或运行数据库 |

## 已执行验证

| 层级 | 结果与证据（均位于 Next Workspace reports） |
| --- | --- |
| UNIT_TEST | Web 36 files / 393 tests、typecheck PASS；r77-web-full.log、r77-web-typecheck.log |
| UNIT_TEST | Worker 57/57 PASS；r77-device-worker-unit.log |
| UNIT_TEST | Service 完整 harness EXIT0；r77-api-service-full.log；故障27/27、健康23/23、Simulator配置26/26、检查点18/18、readiness16/16 |
| Release Build | Service / tests 0 warnings / errors；r77-api-service-build.log、r77-api-fallback-build.log |
| FIXTURE_SERVICE_HTTP | 权限/Site Claims/API边界43 checks PASS；r72-feature-http-20261108-232807-025c8b.json |
| FIXTURE_SERVICE_HTTP | 配置变化14 checks PASS；r77-drift-http-20261108-233146-628518/result.json |
| FIXTURE_SERVICE_BROWSER | 配置变化16 checks PASS，两个分辨率实际Chrome；r77-drift-browser-2026-11-08T15-31-49-266Z/result.json |
| FIXTURE_SERVICE_HTTP/BROWSER | Long Ping13、loss HTTP18、loss browser18 PASS；r77-long-ping-http.log、r77-ping-loss-http.log、r77-ping-loss-browser.log（此前候选cfc801c） |
| SERVICE_E2E | 最终候选显式DLL完整离线门禁 EXIT0，包括真实回环ICMP、计划备份持久化、Worker重连；r77-offline-dotnet.log |
| SERVICE_E2E | 最终候选APPHOST完整离线门禁 EXIT1，ICMP_PERMISSION_DENIED；独立配置/调度子门禁通过；r77-offline-apphost-final.log |
| SIMULATOR_READONLY | 新Worker双AC各6条命令、Raw长度/hash及Facts PASS；r72-product-dual-ac-20261108-233229/summary.json |
| SERVICE_E2E_BROWSER | 广覆盖4项/8个视口布局PASS；r77-final-broad-browser-20261108/result.json |
| SERVICE_E2E_BROWSER | 专项10项：8 PASS / 2 PARTIAL，无页面错误；有效变化历史为空；r77-final-browser-20261108/result.json |
| Runtime integrity | SQLite ok，164 FINALIZED逐项长度/SHA256通过，incomplete0，原检查点/19资产不变；r77-post-upgrade-integrity.json |
| SHORT_RUNTIME_OBSERVATION | 300.006秒/21次采样全部HEALTHY，同一PID21540、integrity issues0；设备连接/任务准入0；r77-runtime-observation-300s.json；8–24h仍NOT_TESTED |
| Final runtime integrity | 观察结束后UTC15:39:47再次校验SQLite ok、164 Raw逐项长度/hash、incomplete0、19资产和R7.6检查点不变；r77-final-runtime-integrity.json；R7.7检查点SHA256另行复核一致 |

运行候选为 build/r77-candidate-api，包含上述产品代码；制品生成在1cd8996提交前，不能把制品元数据中的cfc801c误写为干净1cd8996发布。离线Worker build/r77-netmiko-2ad4698 清单/18固定依赖smoke通过。仅为本地构建候选，非正式发布。

Service：http://127.0.0.1:18771，PID21540，HEALTHY；Vite5174/PID32812保留。仅使用Next数据根 test-data/simulator-live-20261105。升级前152 Raw，采集新增12后164；配置写入0。单库R7.7检查点见运行交接。

短时观察UTC15:34:41–15:39:41，工作集169689088–177496064字节、句柄596–614；没有以短观察推断长期资源稳定或泄漏结论。最后运行完整性复查在观察结束后完成。

## 明确未完成

APPHOST ICMP权限、FIT原生认证/exec-console有效读回、SW有效凭据、整机恢复/失败回滚、受控HA及客户端连续性、Windows SCM安装升级回滚卸载、8–24h长稳均未闭环。短时观察独立记录，不冒充长稳。活动Long Ping故障投影、Syslog/配置自动事件及完整旧产品迁移仍有功能缺口。

后续从 [剩余任务](R7_7_REMAINING_GAPS.md)、[任务池](R7_7_TASKPOOL.md)、[运行交接](R7_7_RUNTIME_HANDOFF.md) 接续。
