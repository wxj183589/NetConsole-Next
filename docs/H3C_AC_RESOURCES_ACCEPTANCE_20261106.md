# H3C AC 资源命令与回显验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。开发基线：`d0aaee6`；本文件记录该基线上的本批未提交修改，最终提交以集成结果为准。本批后端定向测试、合成 HTTP/Netmiko 链路及源码浏览器场景通过；实际 H3C AC 型号兼容仍为 `REAL_DEVICE_PENDING`。

## 原版命令来源

只读研究原版 `D:\study\NetConsole-Workspace\NetConsole`：

- `src/netconsole/services/h3c_ac_collect_service.py:80` 定义必需三条；`:85` 定义可选五条；`:106` 单列 `all verbose`；`:94` 定义 AC 概览。
- `src/netconsole/services/device_command_profile_service.py:224` 的无线 Capability 与 `:298` 的公共 Capability 提供对应命令及设备家族来源。

本批完整采集及浏览器验收覆盖以下 12 条固定只读命令：

| 范围 | 命令 | 本批结果与处理 |
| --- | --- | --- |
| 必需 | `display wlan ap all` | 保留现有 AP 资源投影，补充独立命令回显 Facts |
| 必需 | `display wlan ap all address` | 保留现有地址投影，补充独立命令回显 Facts |
| 必需 | `display wlan ap all radio` | 补齐 Raw 到命令 Facts 的发布与页面读取 |
| 可选 | `display wlan ap all radio verbose filter bbssid` | 固定命令回显证据，`structured=false` |
| 可选 | `display wlan ap all connection-record` | 固定命令回显证据，`structured=false` |
| 可选 | `display wlan ap all radio type` | 固定命令回显证据，`structured=false` |
| 可选 | `display wlan ap unauthenticated` | 固定命令回显证据，`structured=false` |
| 可选 | `display wlan ap all lldp` | 固定命令回显证据，`structured=false` |
| 可选 | `display wlan ap all verbose` | 固定命令回显证据，`structured=false` |
| 概览补充 | `display cpu-usage` | 固定命令回显证据，`structured=false` |
| 概览补充 | `display memory` | 固定命令回显证据，`structured=false` |
| 概览补充 | `display device manuinfo` | 固定命令回显证据，`structured=false` |

原版概览还包括 `display version`、`display device`；Next 已有命令与投影，本批继续复用，没有重复建立概览模型。因此“12 条”是三条必需、六条可选、三条补充概览的测试计划，不表示原版只有 12 条命令。

本批没有引入原版 `display wlan ap address` 别名、按名称生成的 `display wlan ap name <name> verbose`，也没有猜测替代命令。原版 `all verbose` 的 1800 秒读取超时没有直接照搬为 Next 的全局超时。页面 AC 预设只选择必需三条，只有显式提交才执行所选命令。

## 后端与页面行为

`src/NetConsole.Next.Service/H3cAcResources.cs` 集中列出固定命令及回显状态；共享 `H3cCommandPolicy` 负责允许范围。所有命令继续使用现有任务调度、CPython/Netmiko、Profile 凭据与 Raw 链路，没有新增设备连接实现，FIT-AP 仍通过所属 AC 采集。

`H3cFacts.cs` 为每条实际返回的命令生成独立 `command.*` Facts，保存确切命令、回显、捕获时间、Profile/设备/Site scope 和 sealed Raw provenance。多命令 Raw 的 `commands[]` 逐条记录 index、command、outputSha256、segmentSha256、capturedMs；验收按各条 proof 核对，不把 `MULTI` 外层当作单条命令的证明。

Netmiko 已剥离命令回显与提示符的正文，只有在 sealed transport completion 可核验时才认定传输完成。离线片段、残留分页、其他命令边界保留 `PARTIAL`；命令不支持与权限不足保留 `UNSUPPORTED`；空输出保留 `EMPTY`。正常命令事实沿用 `STALE` 状态，并按捕获时间展示。`COLLECTED_RAW_VERIFIED` 说明 Raw 来源与内容可核验，不代表实时状态、完整业务表解析或真实型号兼容。

可选命令与补充概览当前提供回显证据，命令解析器明确标记 `structured=false`；对应 `command.*` Facts 保留命令、状态与正文，不建立结构化业务表。未建立经 fixture 验证的专用表解析器时，不把正文拼接成猜测的 LLDP、连接记录、射频或硬件模型。

`web/src/views/H3cView.vue` 按确切 kind 独立读取这 12 条命令：当前每条 `limit=1`，历史每条 `limit=20`。通用 Facts 页满 100 条时，Radio 等命令仍能显示；通用列表中同 kind 的重复内容被排除。页面展示来源、捕获时间、状态与可折叠转义回显。失败、部分、空与不支持状态优先于不一致的 Raw 已核验标签。任一命令读取被拒绝时清空旧证据；打开页面、切换当前/历史、展开正文均不创建采集任务。

## AP 组身份边界

原版 `src/netconsole/parsers/h3c/ac/wlan_ap_parser.py:118` 解析 `group_name`；`wlan_ap_verbose_parser.py:167` 的 `apid -> ap_id` 是 AP ID，`:168`、`:169` 的 `apgroupname/groupname` 也只表示组名。此次研究未取得稳定的厂商 AP 组 ID 证据。

因此，当前采集得到的名称观察只生成绑定来源 AC 的候选组：

```text
GroupIdentityStatus = CANDIDATE
SourceGroupKey = null
GroupIdentityKey = null
```

不会把组名、AP ID、名称 hash 或内部候选 ID 当作 `SourceGroupKey`。不同 AC 上的同名组分别保留；本批 HTTP 测试实际验证两个 AC 的 `group-a` 不合并。现有 `WirelessGroupProjection` 对来源组 key/evidence 的要求继续生效。只有获得可追溯、经验证的稳定厂商组身份后，才能提升候选状态。

## 本批实际验证

| 验证 | 实际结果 | 证据范围 |
| --- | --- | --- |
| C# `--h3c-ac-resource-tests` | **3/3 PASS** | 固定命令与状态、调度器多命令 sealed Raw/Facts、Catalog 重启、可选失败历史和候选组边界 |
| `python tests/h3c_ac_e2e.py` | **21 checks PASS，EXIT 0** | 实际 HTTP Service → CPython/Netmiko → 合成回环 SSH → sealed Raw → Facts |
| `node tests/h3c_ac_browser_e2e.mjs` | **7 scenarios PASS，EXIT 0** | Chrome 源码 Vite UI + API mock，六张截图，两个分辨率，无页面错误或未知 API |
| Python AST、`node --check`、补丁 whitespace | **PASS** | 静态验证，不替代运行验收 |

后端和 HTTP 结果由主 Agent 执行确认，未另存独立日志文件；本批交接记录为 tool session `26440`（C# 3/3）与 `57813`（HTTP 21，主 Agent 已轮询退出码 0）。本文件不将这些会话号当作可独立重放的日志制品。完整集成 harness 的最终结果另由主 Agent 报告。

HTTP 的 21 项检查覆盖：12 条命令的确切 Raw/hash/scope/Facts、Radio 独立当前快照、管理员及 Site 访问边界、两个 AC 同名候选组、命令不支持/权限不足/分页/空输出、失败后历史保留、两次重启后 Raw/当前/历史不变且不重新采集、API/任务定义/Raw 不含测试密码、只执行显式固定只读命令，以及进程和临时测试数据回收。SSH 端点是合成回环模拟器；该结果不证明现场设备兼容。

浏览器报告类型为 `CHROME_SOURCE_UI_MOCKED_API_FIXTURE`，`backendExercised=false`、`deviceConnections=0`、`databaseWrites=0`。七个场景为：

1. 通用 Facts 已满 100 条时，12 条命令仍各自读取，且不隐式采集。
2. Radio 来源、时间、Raw 状态完整；设备 HTML 片段按文本显示。
3. 1920×1080、2560×1440、DPR 1 下布局可用，无水平溢出。
4. 当前/历史回显分离，历史上限 20，切换时清除旧历史。
5. 不支持、部分、空、失败状态覆盖不一致的 Raw 已核验标签。
6. 单条读取 403 后清除全部旧 AC 证据，不回退通用列表旧值。
7. AC 预设仅选择必需三条；显式提交发送三条命令及 60000ms 任务计划，运行请求不携带密码，沿用已保存的 Profile 凭据机制。

实际执行时间为 `2026-11-06T15:51:20.956Z` 至 `2026-11-06T15:51:23.153Z`。六张截图均已打开检查：正文、来源、历史、失败状态、拒绝读取和显式任务提交显示清楚；未见遮挡、裁切或水平溢出。截图中的设备信息及 2027 年捕获时间均为合成 fixture。

证据保存在 `D:\study\NetConsole-Next-Workspace\build\h3c-ac-browser\`：

- `acceptance.json`
- `ac-current-1920.png`
- `ac-current-2560.png`
- `ac-current-and-history.png`
- `ac-optional-failure-states.png`
- `ac-read-denied.png`
- `tasks-ac-explicit-selection.png`

## 复现与数据影响

在主仓库运行；后端与 HTTP 脚本要求已有本批 Release Service 与 test harness，脚本自身不构建：

```powershell
dotnet tests/NetConsole.Next.Service.Tests/bin/Release/net10.0/NetConsole.Next.Service.Tests.dll --h3c-ac-resource-tests
python tests/h3c_ac_e2e.py
node tests/h3c_ac_browser_e2e.mjs
```

后端测试及 HTTP 验收仅使用 Next Workspace 下的隔离合成数据，HTTP 脚本退出后回收自建临时数据与服务/SSH 模拟器。浏览器不调用后端、设备或数据库，自建 Vite/Chrome 均已退出，保留上述截图及 JSON。没有访问或修改旧 NetConsole 的 Production/Dev 数据，也未对真实设备执行配置变更。

实际 H3C AC、交换机、路由器及不同 Comware 型号、Telnet、现场长回显和 Windows 离线部署不在本专项证据范围内，继续标记 `REAL_DEVICE_PENDING` 或按各自专项验收记录。本资源 UI/文档子任务未构建、启动后端 Service、提交、Push、Merge 或 Release；后端构建和运行验证由主 Agent 完成。当前分支保留，最终提交与全量验证由主 Agent 汇总。
