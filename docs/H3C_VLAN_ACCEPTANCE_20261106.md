# H3C VLAN 采集与快照验收

日期：2026-11-06。Branch：`codex-A/netconsole-next-r3-dual-ac`。本批源码以本文件所属功能提交为准。整体 `PARTIAL / REAL_DEVICE_PENDING`；仅本地提交，不 Push、Merge、Release。

## 实现

- `display vlan all` 进入既有只读 allowlist、统一任务计划、CPython + Netmiko、Raw manifest 与 Facts 发布链；没有新连接客户端或数据库迁移。
- Comware detail parser 保留 VLAN ID、名称、描述、类型、路由接口、Tagged/Untagged 端口及可选 IP/private VLAN 信息；端口范围原样保留，不推断 PVID 或展开范围。重复、非法、不完整、残余分页、错误命令边界拒绝发布完整表；Name/Description 中的错误或分页字面量不作为 CLI 诊断。
- 每次发布一个 `vlan.table/default` 整表修订。后一次完整表省略的 VLAN 不会留在 Current；旧表保留在历史。PARTIAL、UNSUPPORTED、EMPTY 为独立状态，不回显上次成功表冒充本次结果。
- 任务中心增加默认未选的 VLAN 项；H3C 事实页独立查询该 kind，避免普通前 100 条事实遮蔽 VLAN，展示当前/历史、捕获时间、来源与状态。刷新、切换、读取失败清空旧表并拒绝迟到响应。
- 修复任务命令明细的跨 Site 元数据泄漏：先执行 `DeviceSiteScopeGuard`，再读取 `CurrentSiteScope`；隐藏任务返回 404，匿名返回 401。
- 最终复查补齐结束提示符后再次出现正文的边界拒绝；不能将两段输出拼成完整 VLAN 快照。重复裸提示符和空行仍可接受。

## 验证

| 检查 | 结果与范围 |
| --- | --- |
| Release Service / Tests build | PASS，0 warnings / 0 errors |
| 默认完整 Service harness | PASS；最终正文边界修复后复验，具体 session 见集成检查点 |
| VLAN parser / Raw publication | `H3C_VLAN_PARSER_TESTS=6/6 PASS`；`H3C_TASK_FACT_PUBLICATION_TESTS=14/14 PASS`，纳入默认完整 harness |
| 前端 | typecheck、Vitest 14 files / 107 tests、Vite build PASS；含 H3cView 22 tests、TasksView 3 tests |
| HTTP → Netmiko → 合成 SSH | `py -3.13 tests/h3c_vlan_e2e.py`：25 checks PASS；7 次只读 VLAN 采集、Raw/hash/manifest、Current 删除与历史、partial/unsupported/empty、Site/匿名、两次重启、凭据不泄漏及资源释放 |
| Chrome 源码 UI / 模拟 API | `node tests/h3c_vlan_browser_e2e.mjs`：6/6 场景 PASS，1920×1080 / 2560×1440，errors/unexpected 为空；6 张视口截图已查看，不代表后端或设备连接 |
| Workspace paths | Python 4 tests PASS；`git diff --check` PASS |

浏览器证据：`D:\study\NetConsole-Next-Workspace\build\h3c-vlan-browser\acceptance.json`。HTTP 合成 SSH 在共享分页及最终正文边界修复前执行；这两项修复由后续完整后端 harness 覆盖。未将旧 `8320e2f` 离线候选当作本批 VLAN 验收；当前构建静态资源已更新，本批目标机离线安装尚未执行。

## 数据与限制

仅使用 Next Workspace 隔离 fixture / SQLite / 回环合成 SSH；未访问旧 NetConsole 真实数据根，未执行设备配置变更。自有 Service、SSH、Chrome、Vite 已释放，保留用户预览 PID 90348。该 detail 格式不代表所有 Comware 版本兼容，也未增加 ZTE 或其他厂商 VLAN 采集。真实设备继续 `REAL_DEVICE_PENDING`。
