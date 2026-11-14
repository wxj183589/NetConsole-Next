# NetConsole-Next Development Gap Report

日期：2026-11-11  
范围：当前自主开发总任务的首个可独立交付单元

## 本阶段已补齐

| 项目 | 结果 | 证据 |
|---|---|---|
| 新增设备 DPAPI/凭据库异常 | 已补齐 | 新增设备与凭据替换路由将平台、IO、格式、权限和加密异常转为脱敏 503，并写入失败审计 |
| 新增设备失败回滚 | 已确认并保留 | `Catalog.CreateDeviceInventory` 已在事务失败时回收新 DPAPI 引用；现有 C# 测试通过 |
| SSH 主机密钥状态 | 已补齐 | 地址/端口变更无条件清理 fingerprint、确认标志和连通性结果；协议切换原有清理逻辑保留 |
| 后端主机密钥约束 | 已确认并保留 | 新 SSH profile 要求 STRICT + SHA256 fingerprint；库存编辑拒绝连接身份变更 |
| 跨平台/脱敏测试 | 已补齐 | C# harness 增加 Windows/non-Windows 策略和消息不泄露路径断言 |

## 仍未完成或不能宣称完成

| 项目 | 当前状态 | 后续边界 |
|---|---|---|
| 无线 AC/FIT-AP 真实业务闭环 | 未在本分支实现 | 需要基于可信 H3C AC 证据完成采集、状态、来源、读取模型、feature gate 和真实设备验收；不能用 fixture 代替 |
| R3 本地无线实现与远端 main 合流 | 未执行 | 需要在明确的集成分支上处理 266 个本地独有提交与 18 个远端独有提交的关系 |
| Windows Service/真实 Windows 安装 | 未执行 | 需要单独的 Windows SCM/安装包验收，不以本地 build 代替 |
| GUI 截图与真实设备测试 | 未执行 | 当前只有源码契约、类型检查和浏览器测试基础；没有真实设备或用户现场验收 |
| Workspace layout 校验命令 | 有差距 | 任务要求的 `python -m scripts.check_workspace_layout` 不存在，需后续补齐或以 `tools/workspace_paths.py` 的正式入口替代 |
| 默认测试临时目录权限 | 环境问题 | 当前用户 Temp 创建目录被拒绝；本次用 Workspace 隔离临时根验证，后续应修复测试机权限或统一测试 temp 配置 |
| 独立 DevStatus 仓库 | 未配置 | 当前 Workspace 没有 `NetConsole-Next-DevStatus`；本阶段使用仓库内自主开发记录，不创建空状态仓库 |

