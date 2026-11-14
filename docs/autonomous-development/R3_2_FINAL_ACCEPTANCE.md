# NetConsole-Next R3.2 最终验收

更新时间：2026-11-15（Asia/Shanghai）

## 最终变量

```text
PROJECT=NetConsole-Next
PHASE=R3.2
SCOPE=A+B+C
BRANCH=codex-A/netconsole-next-r3-dual-ac
PRODUCT_START_HEAD=ed02889
PRODUCT_CODE_COMMIT=6a6239c79dcb67b7fe386d4ec9bf90610980bfc4
PRODUCT_PUSH=NO_BY_USER
REMOTE_MAIN_MERGE=NO
RELEASE=NO
R3_2_A_STATUS=PARTIAL
R3_2_B_STATUS=PARTIAL
R3_2_C_STATUS=PARTIAL
R3_2_D=OUT_OF_SCOPE_BY_USER
SERVICE_BUILD=PASS
WEB_TESTS=PASS_36_FILES_451_TESTS
WEB_TYPECHECK=PASS
WEB_BUILD=PASS
SERVICE_FULL_REGRESSION=PASS
LOCAL_MANAGER_TESTS=PASS
SCM_RUNTIME=BLOCKED_ADMIN_PRIVILEGE
H3C_REAL_READONLY=PARTIAL_AC1_AC2_PASS_SW_AUTH_FIT_AP_CONNECTION_LOST
WIRELESS_SERVICE_E2E=BLOCKED_GLOBAL_SINGLE_INSTANCE_MUTEX
```

## 数据与安全

```text
PRODUCTION_DATA_MODIFIED=0
DEV_REAL_DATA_MODIFIED=0
FULL_DATA_ROOT_COPY=0
DATABASES_REMOVED=0/0
DEVICE_CONFIGURATION_WRITES=0
MAINTENANCE_AUTHORIZED=NO
ROOT_D_STUDY_FILES_CREATED=0
```

本轮只在 `D:\study\NetConsole-Next-Workspace` 下创建隔离报告和候选数据库。没有读写 `D:\NetConsoleData` 或 `D:\NetConsoleData-dev`，没有修改 `D:\study\AGENTS.md`、`D:\study\.git` 或其他一级项目，没有清理既有静态资源、Raw、SQLite、checkpoint 或历史报告。

## 验收判定

R3.2 不能标记为全量 PASS：A 受管理员权限阻断，B 的 SW/FIT-AP 未完成认证/会话，C 的真实 HTTP 服务链路受现有全局单实例阻断。代码和离线/组件验证已完成，真实 SCM、真实 AC Syslog、AP 现场身份、FaultCenter UI 服务级闭环和长稳定性仍不得用模拟结果替代。

R3.2-D 的 8–24 小时稳定性按用户要求明确排除，不作为本轮失败项，也不作为已完成项。

## 后续门禁

1. 在管理员维护窗口确认并处理现有 Service 实例归属，再执行 canonical Windows SCM lifecycle 和升级数据保留脚本。
2. 在授权的真实设备窗口补齐 SW 凭据、FIT-AP 会话，并以产品 Service 的 Raw/Facts/无线投影作为证据；禁止猜测凭据或执行配置写入。
3. 关闭单实例阻断后，重新执行隔离 HTTP E2E 和真实浏览器证据，确认 down/open、up/resolved、AP 唯一身份和原始证据跳转。

