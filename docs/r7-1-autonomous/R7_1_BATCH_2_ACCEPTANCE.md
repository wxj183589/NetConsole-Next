# R7.1 第二至第五批集成验收

日期：2026-11-08（Asia/Shanghai）；Agent：codex-A；分支：`codex-A/netconsole-next-r3-dual-ac`。

状态：**PARTIAL**。本报告汇总第一批之后第二至第五批的实际产品变更、定向证据与本批集成 Gate；后续 LLDP 增量、自然到期结果和 Git 交接由根集成补录。没有把本地 fixture、TCP 可达或单次采集成功提升为设备业务恢复、整机恢复或现场长期运行验收。

## 实际交付

### 配置计划、完整备份与容量

- 完整原件计划补齐固定目标明细、逐档案历史入口、最近执行时间和真实 LastBatchId 结果。查看最近执行只读，不隐式启动采集。
- 新增产品计划“立即执行”和 `POST /api/system/config-backup-schedules/{id}/run`，复用当前所有者、权限、Site、模块、凭据和目标准入。暂停计划允许明确执行一次，原启停状态与自然到期时间不变；手动、自动共用防重叠和全局四计划并发约束。
- 私有完整配置仍与公开脱敏视图分离；完整比较仅下载后端脱敏 JSON。任意历史完整配置传输、staging 与 native replace 未实现为已验证能力，不能以描述恢复代替整机恢复。
- 完整备份版本按结构有效 manifest 单调分配；正文损坏仍占用原版本。读取、变更和恢复输入继续校验实际字节数、SHA256、完整输出和脱敏对应关系。
- 新增非破坏性发布准入，默认完整备份目录全局 1 GiB、每档案 1000 份；连接前和发布前分别检查，不同档案共用发布锁。损坏、孤立和临时文件也计容量，遇链接路径拒绝。配额错误不删除旧历史，旧历史仍可查看、比较和下载。
- 删除运行中计划后继续观察真实批次终态，再释放并发槽位与 retention pin，不重建被删计划；完成批次内存元数据最多保留 128 份，不删除备份文件。

容量设置为 `NETCONSOLE_NEXT_COMPLETE_CONFIG_MAX_STORED_BYTES`（1 MiB–100 GiB）和 `NETCONSOLE_NEXT_COMPLETE_CONFIG_MAX_BACKUPS_PER_PROFILE`（1–100000），重启 Service 生效；非法设置明确启动失败。该配额仅覆盖专用 `backup/config-complete` 存储，不是整个 DataRoot 的磁盘配额。本轮未做运行盘满测试或自动清理。

### Syslog 事件分类

- 复用实际封存 Raw 查询，增加 H3C IFNET/CFGMAN/WMESH 五种完整消息标识分类，以及确切类型和 UNKNOWN 筛选；页面及 CSV 展示事件族、类型，保留原文与 Raw 引用。
- 不从普通关键词、AP 名称或站点猜测事件；错配族、边界错误、多标识及正则超时均保留 UNKNOWN。既有授权、路径完整性、查询预算和 partial 状态继续生效。
- 本次分类来源为旧产品源码与测试样本的只读对照。分类不代表设备状态、链路恢复或业务恢复；没有读取旧真实数据。

### 故障工作台与当前页 CSV

- 故障事件可精确筛选同一设备并进入原设备配置历史；来源链接只使用既有 TASK_RUN、DIAGNOSTIC、WIRELESS_RUNTIME 只读 API，不制造未知来源 URL。
- Dashboard 增加 Syslog、拓扑及页内故障工作台入口。页内跳转只焦点与滚动，不请求网络。
- CSV 仅导出当前已加载页及其全部保留处理历史，记录 EVENT/HISTORY、身份、状态、次数、来源证据、时间、已应用筛选、页码、page size、匹配总数和加载时间；不补查其它页。未提交的筛选输入不重标旧事实，加载、失败与空页禁用导出。
- CSV 处理引号、换行和 BOM，并防御控制/格式字符之后的公式前导符；自由文本按口令、JSON 键值、Basic/Bearer、access/refresh token、SNMP community、URL 凭据及私钥模式脱敏。该处理依赖模式，页面明确要求分享前复核，不宣称任意文本完全无敏感信息。
- 同一挂载组件的 username/role 变化同步清空旧证据、加载快照和深链并使旧响应失效；隐藏 KeepAlive 页面清空快照且不主动加载，重新激活再读取。
- 浏览器 CSV 断言验证 BOM、完整引号、每行列数；每个故障场景先重置筛选，避免空数组断言误报通过。

### 部署路径与 Worker 生命周期

- Service、Local Manager、doctor 与 open/credential/token 入口在文件系统调用前词法拒绝旧数据根及其 Windows 别名和设备 namespace；同前缀独立目录不误拒绝。
- Local Manager 对受管根和打开目标从文件系统根逐级拒绝 reparse/non-directory，doctor 同步复用，避免先沿祖先 junction 读取子目录。拒绝后不进入 marker、HTTP 或 worker 探测。
- Worker 验证覆盖自身子进程 Dispose、在途取消/Abort、COMMAND_TIMEOUT、EOF 与 scheduler shutdown，实际观察退出后才返还预算。Dispose 沿用 Abort 自有进程树，不宣称 graceful close handshake 或已实测满 45 秒兜底计时。
- 安装失败补偿仍有未检查 delete cleanup 退出码、ACL/EventSource 未完整补偿的缺口；本轮只读核验并如实记录，未声明已修复，也未执行 SCM/ACL/事件源变更。

## 已执行证据

| 范围 | 结果与实际边界 |
| --- | --- |
| 配置 Service | complete batch 11/11、simulator config 19/19、schedule 19/19；隔离 Release harness 构建 0 warnings / 0 errors。包含容量、损坏最高版本、并发配额、运行中删除回收和 run-now 权限/周期边界。 |
| 配置 Web | 三文件 34/34 PASS；精确目标、历史/最近批次、立即执行、容量错误、轮询和 KeepAlive 等定向覆盖。 |
| 故障与快捷入口 Web | FaultCenter 13 + Dashboard 3 + OperationalHealth 5 = 21/21 PASS；typecheck PASS。对抗凭据/控制字符使用隔离组件 fixture，没有注入运行数据。 |
| Syslog | SyslogQuery 8/8、SyslogEvents 6/6，Vue 6/6 PASS；实际 RawSegmentWriter 隔离封存文件及本机 UDP 测试，不是 H3C 设备发日志验收。 |
| 部署与 Worker | Local Manager 最终 13/13 PASS；Worker budget 9 组、Python Worker 46/46；RuntimeSettings 定向 EXIT 0；Local Manager Release 0 warnings / 0 errors。实际自有合成进程退出、junction 和仅 fixture marker 的 ACL 拒绝路径均有覆盖。 |
| 独立真实 HTTP | `reports/r71-feature-http-20261108-012452-8425e4.json`，23 组 PASS；真实隔离 Service 的 ADMIN/SUPERADMIN、401/403、CSRF、会话 claims 收缩、模块关闭和现存事实读取。没有设备连接或 UDP 监听；missing-plan run-now 准入不是执行成功。 |
| 真实浏览器故障复核 | `reports/r71-browser-fault-review-20261108/result.json`，login/dashboardShortcuts/faultExport/faultNavigation 连续 PASS，API/page/console 错误 0。真实 CSV 2134 bytes、1 EVENT + 2 HISTORY 与原 API 比对，导出额外故障查询 0。8 张 1920/2560 截图逐张查看，无横向溢出或控件覆盖。 |
| 真实浏览器配置 run-now | `reports/r71-schedule-browser-2026-11-07T17-26-09-781Z/result.json`，单精确 SW 计划立即执行返回 202，批次 `d88f1bd72d724596bbfd997bbbb72950` SUCCEEDED 1/0；私有与脱敏版本字节数/SHA256均核对。计划已暂停，最近执行仍指向同批次。未执行配置写入。 |
| 本批完整 Web 与静态构建 | `reports/r71-web-closure-final.log`：27 文件、305 项 PASS；`reports/r71-typecheck-closure-final.log`：typecheck PASS；根集成确认静态 build PASS。`reports/r71-static-assets-reachability.json`：27 个可达文件，本次新增 19 个 JS/CSS chunk 均可达，newUnreachable 为空。 |
| 本批完整 Service | `reports/r71-service-harness-quota-final.log`：完整 quota Service harness EXIT 0，包含容量与既有调度、恢复、故障注入、取消和生命周期覆盖；不将之后另批 LLDP 增量算作本次已验证。 |
| 最终静态 UI 浏览器 | `reports/r71-browser-static-closure-20261108/result.json`：UI/API 同为 `http://127.0.0.1:18771`；login、dashboardShortcuts、faultExport、faultCenter、faultNavigation、completePreview、syslog 共 7 步 PASS，API/page/console 错误均 0，整体 PASS_WITH_LIMITATIONS。11 个场景的 1920/2560 共 22 张截图均逐张视觉检查，未见页面横向溢出、控件遮挡或重叠；22 项 document 宽度均等于 viewport。浏览器未连接设备、未修改策略、未采集/创建计划/恢复，也未发送 UDP。 |
| 静态 UI 的 Syslog 分类与下载 | 同上静态浏览器结果：读取 4 条真实封存的 localhost 合成消息，验证 MESH_LINKUP、IFNET_PHY_UPDOWN 与多标识冲突 UNKNOWN 精确筛选；CSV 1066 bytes / 4 records，保留原文与 Raw 引用。receiver 当前运行 3 received / 3 persisted / 0 dropped；这是本机合成消息链路，不能视为 H3C 设备 Syslog 互操作验收。 |

上述相对 reports 路径均位于 `D:\study\NetConsole-Next-Workspace`。失败的首轮脚本、选择器和初始化证据按各专项报告保留，不通过伪造响应抹除失败。浏览器请求转发真实 API，没有替换事实返回。

## 根集成待补录

- 本批完整 Web、quota Service harness、typecheck、静态构建与浏览器证据已录入上表；**后续 LLDP 增量及最终变更后的检查、最终全仓 diff 检查由根集成填写**，不沿用本批结果覆盖之后的代码变更。
- 自然到期 AC1 计划 `6334c82406a348689bf29dd186137351`：真实重启后已确认 enabled=true、NEVER_RUN、NextRunMs=`1794075400761` 原样保留，原定本地 2026-11-08 02:16:40.761 自然到期。**最终到期执行、批次终态、版本校验及随后暂停结果待根集成观察补录**；没有改 due、改时钟或提前制造成功。
- 集成 commit、Push、DevStatus commit/Push、最终 runtime PID/版本与保留计划状态：**由根集成统一记录**。本报告编写子任务未提交、未推送、未控制共享服务。

## 未完成与数据影响

原生整机恢复、受控手动 HA 切换、FIT 直连认证、非空真实 LLDP CSV、真实设备 Syslog 分类、长期运行及现场 Windows SCM 安装/重启/断电仍按各专项阻断或待验收；fixture 与浏览器只读证据不替代这些项目。当前未覆盖 125% 缩放验收。

仅使用 Next Workspace 内测试与隔离 runtime；未访问、复制或修改 `D:\NetConsoleData`、`D:\NetConsoleData-dev`，未把旧项目运行数据作为测试目标。运行产品采集证据只增加授权只读备份及相应元数据，未执行配置写入、整机恢复或 HA 切换；暂停的 SW 计划及自然到期 AC1 计划由根集成统一收尾。整产品维持 **PARTIAL**。

详情：本目录 `R7_1_CONFIG_MANAGEMENT_REPORT.md`、`R7_1_FAULT_RECOVERY_REPORT.md`、`R7_1_SYSLOG_EVENT_CLASSIFICATION_REPORT.md`、`R7_1_DEPLOYMENT_WORKER_LIFECYCLE_REPORT.md`。
