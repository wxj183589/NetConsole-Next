# R7.1 持久故障中心与有限只读补采

日期：2026-11-08（Asia/Shanghai）

状态：PARTIAL。持久生命周期、权限及恢复证据边界已实现并通过定向测试；真实浏览器验证了本地证据投影、历史、筛选和存量配置预览。未人为制造宕机、HA 故障或宣称正常设备连接成功即为业务恢复。

## 产品行为

- 独立故障中心通过既有健康事实投影事件，按 Site、设备、档案、类型和精确资源聚合；不会因刷新重复生成观测历史。
- 事件保存 OPEN → ACKNOWLEDGED → RESOLVED 生命周期，保留首次、最近观测、Raw/Task/Fact/诊断证据和最近 50 次有效处理记录。确认仅表示知悉，核验失败保持原状态并留下原因。
- 接口恢复须为同接口 IdentityKey 的更新且新鲜 Raw-verified UP 事实；ICMP/TCP 丢包恢复须协议、端点和端口匹配；AP 恢复要求同资源、原 AC/Site、所有来源更新且新鲜 LIVE_VERIFIED/SUCCESS，以及有效 RUNNING_MASTER 聚合。备用 AC 单独在线不证明业务恢复。
- 显式逐故障逐设备启用的只读补采默认关闭，等待完整 5–1440 分钟后运行，最多 1–3 轮；每轮最多 3 次瞬态探测，认证或密钥失败停止。只复用现有版本/接口采集计划，不含配置 Writer。
- 策略固定原始账号、角色、Site/claims revision、设备、档案、主机与端口。执行及后续证据核验均重新检查；等待补采期间权限撤销持久化为 BLOCKED。RUNNING 跨重启转为 INTERRUPTED，禁止重放网络准入。
- 状态文件为独立 DataRoot/operations/fault-center.json：最多 1000 事件、64 策略、8 MiB，临时文件 Flush(true) 后原子替换；写入失败不前移内存 authority。损坏文件保留并关闭该可选功能，不阻止主服务启动。
- 自动发现每 15 秒读取一页 25 台授权库存的本地事实，不主动连接设备；设备健康、无线证据及诊断的既有有界查询限制继续生效。
- API 与 UI 支持状态、设备 ID、类型、时间筛选、分页、历史展开、确认、证据核验和有限策略；所有写操作限当前活动 SUPERADMIN，所有记录按当前 Site scope 过滤。
- 普通只读运维可从事件一键精确筛选该设备故障，保留已有状态/类型/时间条件并回到第一页；重置按钮清空条件。具备设备与档案标识时，复用配置备份页面的精确 deviceId 上下文进入该设备配置历史。来源任务、诊断与 AP 运行证据只链接现有只读 API，未知来源不生成入口；这些入口不启动连接、补采或配置写入。
- 故障中心新增当前已加载页事件及全部保留处理历史 CSV：EVENT/HISTORY 行保留 Site、设备、档案、资源、状态、次数、来源证据与时间，重复记录已应用筛选、页码、page size、匹配总数和加载时间。导出不补查其他页、不发新请求；编辑但未提交的筛选不重标旧结果。CSV 对公式起始字符防护，自由文本按口令、token、community、Bearer、URL 凭据及私钥模式脱敏，明确分享前复核；不宣称任意自由文本完全无敏感信息。加载、错误及空页禁用导出。
- Dashboard 常用操作新增既有工具集中的 Syslog 查询入口、拓扑与资产入口以及页内故障中心焦点/滚动入口。检查旧产品 Dashboard 与无线告警页面未发现可直接复用的故障历史 CSV；Next 复用既有本地 Blob 下载方式，没有新增导出服务或数据库查询。

## 验证证据

- C# --fault-center-tests：14/14 PASS，含精确资源恢复、AP 来源证据、有效历史上限、持久化/损坏/失败提交、重启不重放、Site/角色、预算、认证失败、端点变化、等待补采时账号/端点撤销；测试使用隔离 catalog 和拒绝连接的 factory，无设备连接。
- Service tests 构建：0 warnings / 0 errors。
- 完整 harness 首次暴露结构合法但含 null 事件/策略的语义损坏文件会抛 InvalidDataException；构造器已显式捕获该类型，校验在解引用前拒绝 null。保留原文件、可选功能 fail-closed，专项 14/14 重新通过；集成工作流已报告第一批完整 harness 最终 PASS，日志及最终 Gate 由集成工作流归档。
- Web FaultCenter.test.ts + OperationalHealth.test.ts：第二批 13/13 PASS（故障中心 8、设备健康 5）；覆盖只读角色精确筛选/重置、配置 deviceId 上下文、来源路径编码及无伪造来源入口。Typecheck PASS。
- 真实 Chrome → Vue → 认证 API，tests/r71_browser_acceptance.mjs，UI 5174/API 18771，隔离 simulator-live-20261105 数据根。
- 首轮故障筛选 exact selector 因缺显式 aria-label 超时，证据保留于 reports/r71-browser-20261108；补齐无障碍标签后，reports/r71-browser-20261108-rerun/result.json 四步全部 PASS，API/page/console 错误均为 0。
- 故障中心读取 3 项存量事件、处理历史，OPEN/ACKNOWLEDGED/RESOLVED 筛选、精确设备/类型/时间空态均验证；未启用任何补采策略。
- 完整备份范围实际解析 1 台 AC1；后续浏览器复验读取 7 份已有完整备份、脱敏配置和上下文比较。真实下载 4522 bytes 脱敏 cfg，逐字节匹配页面显示；真实下载比较 JSON，保留所选 before/after snapshot ID 和 contexts。未启动采集、计划、恢复或写入。
- 存量 COMPLETE_RUNNING 计划为 0，准确呈现计划空态；暂无可供真实浏览器检查的固定目标详情/最近执行，未制造成功记录。组件及测试的计划操作证据由配置工作流记录。
- reports/r71-browser-syslog-udp-20261108/result.json：login/completePreview/syslog 全 PASS，API/page/console 错误为 0。父工作流发送单条本机 UDP `<134>R71_LOCAL_TEST browser CSV acceptance local fixture only` 后，刷新接收状态显示 enabled/listening、已接收 1 / 已落盘 1 / 丢弃 0。
- Syslog 实际查询返回该单条已封存消息、来源 127.0.0.1、facility 16 / severity 6、Raw 路径及行号；真实下载 syslog-query.csv（280 bytes / 1 行数据），校验列名和 Raw 引用。IP/级别/文本空筛选及导出禁用通过。这是本机接收链路证据，不是设备 Syslog 互操作验收。
- 首次修复后 12 张、后续接收/导出复验 8 张 1920×1080 / 2560×1440 full-page 截图已逐张视觉检查；全部 document.scrollWidth 等于 viewport，无横向溢出或控件覆盖。
- reports/r71-browser-topology-20261108/result.json：真实拓扑读取 15 个档案 claims 全成功，均无 LLDP 关系；页面准确展示 6 台 UNKNOWN 资产与 0 关系，三个精确筛选仅提供全部选项、CSV disabled。两分辨率截图逐张检查无溢出。没有制造事实；非空精确筛选和 CSV 仍缺真实存量验收证据。
- reports/r71-browser-fault-navigation-20261108/result.json：login/faultNavigation PASS，API/page/console 错误为 0。实际点击 r68-ac2 的“仅看此设备故障”，精确查询得到 1 项，重置恢复 3 项；浏览器认证 fetch 来源 TASK_RUN 36015fe99409433ca153b14a4249f3bd，返回同一 ID、FAILED 状态。实际点击配置历史入口 /config-backups?deviceId=r68-ac2，核对设备 API 的原始档案 fc7145961a2a4bcfbabb2d14347c2d85，并展示 AC2 运行配置历史空态。没有假造配置记录或任务 UI 深链。
- 本次导航 4 张 1920×1080 / 2560×1440 full-page 截图已逐张视觉检查，无横向溢出或控件覆盖；设备连接数与策略修改数均为 0。该证据验证入口和原设备关联，不代表故障已恢复。
- tests/r71_feature_http_e2e.py --service-dll build/r71-service/NetConsole.Next.Service.dll：真实隔离 Service HTTP 23 组 PASS，报告 reports/r71-feature-http-20261108-012452-8425e4.json。当前 schema 只有 ADMIN/SUPERADMIN，以 ADMIN 验证这些功能的只读权限：run-now、完整目标预览、policy/ACK/verify/sync 均 403；匿名 401、无 CSRF 的 POST 403。原会话 claims 从 local/site-a/site-b 收缩为 site-a 后，local Syslog 403、跨设备故障筛选为空，全部撤销后两类读取均 403；SUPERADMIN 仍可读取原始 3 条故障/1 条封存 Syslog。禁用 config-backups/tools 后新 targets/run-now/schedules/Syslog 路由均 404，重新启用可读取原存量。
- 上述 HTTP gate 只使用最小合成故障与一条封存 Syslog，不打开设备传输或 UDP 监听；未准入任何设备采集/探测 run。run-now 只验证 missing-plan 路由准入及角色拒绝，不表示执行成功。首次脚本在目录初始化前放入 fixture，触发 Catalog 对非空未标记目录的合法拒绝；已改为先实际初始化空根、停子进程、写最小 fixture、重新启动，保留两次失败证据。生产代码无修改。
- 第三批 Web FaultCenter 10 + Dashboard 3 + OperationalHealth 5 = 18/18 PASS，Typecheck PASS；新增测试覆盖实际本地下载、仅加载页/已应用查询、保留历史、引号换行、公式防护与凭据模式脱敏、加载/空/失败禁用、快捷路由和本地焦点动作。
- reports/r71-browser-fault-export-20261108-final/result.json：真实 login/dashboardShortcuts/faultExport 全 PASS，API/page/console 错误为 0。实际点击 Syslog 和拓扑入口抵达对应现有页面，故障入口焦点落在当前页故障工作台。实际精确筛选 r68-ac2 后下载 fault-history-page-1.csv，2134 bytes、1 EVENT + 2 HISTORY，逐项与真实返回的身份、状态、次数、证据及时间核对，CSV 范围保留已应用 deviceId、page/page size/total；输入未提交筛选后仍导出原事实。下载额外故障查询数 0，空查询后导出禁用。4 张 1920×1080 / 2560×1440 截图逐张视觉检查，无页面横向溢出或控件覆盖；无设备连接和策略修改。
- 该浏览器 gate 首次已通过；为取得完全加载后的截图追加等待时，过渡动画同时存在两个 loading mask 导致测试 strict selector 失败，保留 reports/r71-browser-fault-export-20261108-settled。等待改为所有 loading mask 移除后最终复验通过；产品代码无需改动。
- 独立复核修复 CSV 两处实际缺口：JSON 引号键/转义值、Basic、access/refresh token、SNMP read/write community 与截断私钥/口令输出补充脱敏；公式防护覆盖前导 Unicode 控制和格式字符（含 NUL、ESC、零宽字符）。Dashboard 传入 username/role 身份键，变化时同步清空当前页、加载范围与深链、失效旧响应；KeepAlive 隐藏时清空快照，身份变化不触发隐藏页请求，重新激活后重新读取。
- 复核后 FaultCenter 13 + Dashboard 3 + OperationalHealth 5 = 21/21 PASS，Typecheck PASS。新增测试覆盖已挂载身份切换及旧请求晚到、隐藏缓存组件收到身份变化、JSON/转义及截断敏感内容、控制字符后的公式；没有使用伪造角色枚举。
- 浏览器断言同步加严：CSV BOM、完整引号和每行列数均验证；每个故障场景开始前显式重置已应用筛选，避免上一个场景的空筛选造成空数组 every() 误判或导航不可达。reports/r71-browser-fault-review-20261108/result.json：login/dashboardShortcuts/faultExport/faultNavigation 连续 PASS，API/page/console 错误 0，导出无额外故障查询；8 张 1920/2560 截图逐张检查无页面横向溢出或控件覆盖。该浏览器使用存量数据，敏感值/控制字符边界由隔离组件测试验证，未向运行数据植入凭据或恶意公式。

## 剩余边界

不含配置差异事件自动关联、外部告警通知投递、无限历史库或无人授权自动修复。有限策略的真实故障恢复与生产长期运行仍需实际业务验收；持久化及虚拟连接测试不替代此验收。整产品状态保持 PARTIAL。

仅操作 Next Workspace 内隔离运行和测试数据；未读取旧 NetConsole 两个真实数据根。代码和本地状态由父工作流统一集成、提交及交接。
