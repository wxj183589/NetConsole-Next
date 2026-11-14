# FIT-AP 默认登录与远程控制台闭环

更新时间：2026-11-15（Asia/Shanghai）

## 本轮结论

FIT-AP 继续由无线业务 AC/AP 视图管理，不作为普通设备库存单独登记。用户可从 AP 行直接远程连接；连接前服务端根据最新无线观察唯一确定当前主用 AC，核对 AP 管理地址、远程控制台状态和连接设置，然后创建受限只读终端会话。

普通用户界面不要求选择 Credential Vault、Credential Reference、Credential Authority 或 SSH Profile。连接设置只展示协议、端口、登录方式、用户名和密码；密码只在运行时使用，或由服务端透明保存为受保护的 opaque key。默认兼容密码只允许通过运行环境 `NETCONSOLE_NEXT_FIT_AP_DEFAULT_PASSWORD` 注入，值不写入源码、配置文件、日志、Raw、Facts、Git 或报告。

## 机器可读状态

```text
TASK=FIT_AP_REMOTE_CONSOLE
PRODUCT_START_HEAD=c7db3f8
DEFAULT_LOGIN=IMPLEMENTED_RUNTIME_ENV_ONLY
DEFAULT_PROTOCOL=TELNET
DEFAULT_PORT=23
DEFAULT_AUTH_MODE=PASSWORD_ONLY
DEFAULT_USERNAME=EMPTY
GLOBAL_OVERRIDE=PASS_UNIT
AC_OVERRIDE=PASS_UNIT
AP_OVERRIDE=PASS_UNIT
OVERRIDE_PRIORITY=AP>AC>GLOBAL>DEFAULT
PASSWORD_API_EXPOSURE=CONFIGURED_BOOLEAN_ONLY
ACTIVE_AC_RESOLUTION=IMPLEMENTED_FRESH_UNIQUE_RUNNING_MASTER
ACTIVE_AC_AMBIGUOUS=BLOCKS_AUTOMATIC_SELECTION
AP_ADDRESS_RESOLUTION=IMPLEMENTED_SAME_CAPTURED_AC_FACT
REMOTE_CONSOLE_CHECK=IMPLEMENTED_ENABLED_DISABLED_UNKNOWN
REMOTE_CONSOLE_ENABLE=IMPLEMENTED_EXPLICIT_CONFIRMATION_REQUIRED
REMOTE_CONSOLE_ENABLE_SCOPE=ALL_AP_ONLY_VERIFIED
FIT_AP_PASSWORD_ONLY=IMPLEMENTED_EMPTY_USERNAME
FIT_AP_SESSION=IMPLEMENTED_DIRECT_EPHEMERAL_PASSWORD
FIT_AP_READONLY_COMMAND=IMPLEMENTED_DISPLAY_VERSION
PASSWORD_ONLY=IMPLEMENTED_EMPTY_USERNAME
USERNAME_PASSWORD=IMPLEMENTED_SETTINGS
FIT_AP_TELNET=PASS_HISTORICAL_REAL_AND_IMPLEMENTED_PRODUCT_PATH
FIT_AP_READONLY_COMMAND_RESULT=PASS_HISTORICAL_DISPLAY_VERSION
RAW_SEGMENT=IMPLEMENTED_FIT_AP_SOURCE_ISOLATION
FACT_PERSISTENCE=IMPLEMENTED_DEVICE_IDENTITY
FACT_QUERY_API=IMPLEMENTED
UI_REMOTE_CONNECT=IMPLEMENTED
UI_CONNECTION_SETTINGS=IMPLEMENTED
RAW_FINALIZED=PASS_TARGETED_ISOLATED_CATALOG_TEST
FACTS_PERSISTED=PASS_TARGETED_ISOLATED_CATALOG_TEST
API_QUERY=IMPLEMENTED_CODE_TARGETED_TEST
UI_RENDER=IMPLEMENTED_CODE_WEB_REGRESSION
WEB_TYPECHECK=PASS
WEB_VITEST=PASS_36_FILES_451_TESTS
SERVICE_BUILD=PASS_0_WARNINGS_0_ERRORS
SERVICE_WIRELESS_TESTS=PASS
SERVICE_FULL_REGRESSION=PASS
REAL_SW_DIRECT_LOGIN=PASS_HISTORICAL_EVIDENCE
REAL_FIT_AP_PASSWORD_ONLY_TELNET=PASS_HISTORICAL_EVIDENCE
CURRENT_PRODUCT_HTTP_E2E=NOT_VERIFIED_EXISTING_SERVICE_MUTEX
BROWSER_E2E=NOT_RUN
WORKER_TEST=PASS_FULL_HARNESS
BRANCH=codex-A/netconsole-next-r3-dual-ac
FINAL_HEAD=58febd2
COMMITS=58febd2
PUSH=NO
RELEASE=NO
FINAL_STATUS=PARTIAL
```

## 服务端边界

- `FitApRemoteConsoleService` 只接受当前无线投影中的 AP 资源 ID，不从设备库存另建 FIT-AP 设备。
- 主用 AC 必须是新鲜且唯一的 `RUNNING_MASTER` 观察；多个候选或过期数据不会按 IP、名称或排序猜测。
- 多个最新候选时界面列出 AC 名称和稳定设备 ID，用户明确选择后才继续；选择通过本次请求传递，不写成永久 IP 绑定。
- AP 地址必须来自同一时间点的 AC `display wlan ap all address` 事实，并按精确 AP 名称匹配。
- 只有明确观测为 `DISABLED` 时才要求开启远程控制台；`UNKNOWN` 允许先尝试已配置的直接连接。
- 当前版本只实现已验证的 Telnet 入口。Password-only 模式向 Netmiko 使用空用户名，不预先发送用户名。
- AC 侧开启动作仅在明确确认后执行；当前设备版本只验证了 `all` 范围，因此界面会明确提示可能影响该主用 AC 管理的其他 AP。
- FIT-AP 版本采集先写入 `raw/fit-ap/<resource-hash>/...` 的单命令 sealed Raw，再写入 `fit_ap_facts`；事实只保存版本等只读解析结果、Raw 路径和校验值，不保存密码。

## API 与界面

```text
GET  /api/system/wireless/ap-resources/{id}/remote-console
GET  /api/system/wireless/ap-resources/{id}/remote-console/settings
PUT  /api/system/wireless/ap-resources/{id}/remote-console/settings
POST /api/system/wireless/ap-resources/{id}/remote-console/enable
POST /api/system/wireless/ap-resources/{id}/remote-console/session
GET  /api/system/wireless/ap-resources/{id}/remote-console/facts
```

无线页提供“远程连接”和“连接设置”。启用远程控制台的配置写入仍受 SUPERADMIN 保护；普通连接不需要先建立复杂凭据资料。终端页通过受限会话 ID 打开 AP 控制台，并保留只读会话边界。

## 真实设备证据与限制

此前真实设备证据已经确认：SW 可用用户提供的直接用户名密码 SSH 登录；FIT-AP 的当前主用 AC 已通过实时 AC/AP 关系确认，执行过隔离模拟 AC 的远程控制台开启动作后，FIT-AP 以空用户名、Password-only Telnet 成功登录并完成 `display version`。证据位于现有 `reports/r3.2-*` 目录，报告不包含密码内容。

本轮代码和 API 已完成 Raw/Facts 链路实现并由定向测试覆盖，但当前产品 HTTP 级真实设备回归仍受已有 Service 单实例互斥阻断，因此不能把独立 worker 的历史证据冒充为当前产品端 Raw/Facts 实时 PASS。未执行浏览器录制、生产 AC 操作、Push、发布或长稳测试。

## 主要实现文件

- `src/NetConsole.Next.Service/FitApRemoteConsole.cs`
- `src/NetConsole.Next.Service/Catalog.FitAp.cs`
- `src/NetConsole.Next.Service/RawSegmentWriter.cs`
- `src/NetConsole.Next.Service/TerminalSessionManager.cs`
- `src/NetConsole.Next.Service/Program.cs`
- `web/src/views/WirelessView.vue`
- `web/src/views/TerminalView.vue`
- `web/src/api/client.ts`
- `tests/NetConsole.Next.Service.Tests/FitApRemoteConsoleTests.cs`
