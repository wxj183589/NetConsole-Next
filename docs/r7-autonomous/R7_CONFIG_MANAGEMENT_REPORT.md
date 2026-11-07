# R7.0 配置管理、上下文比较与原生检查点验证

状态：**PARTIAL**。完整备份历史比较、脱敏下载、实时恢复预览和检查点验证状态机已实现。授权 SW 的实际 `configuration commit` 未创建任何检查点，原生整机恢复为 **LIVE BLOCKED**；任意本地历史完整配置恢复也仍未开放。

## 产品改动

- 完整备份页面展示设备 / Site、型号 / 软件版本、采集任务、连接端点、备份版本 / 格式版本，以及原始配置与脱敏副本的字节数和 SHA256。
- 当前设备任意两份已验证完整历史备份可独立选择、比较并导出脱敏 JSON。下载配置时重新请求后端校验后的脱敏副本，下载文件名明确含 `redacted`，不能将其作为完整恢复原件。
- 比较按配置段保留上下文、重复命令和命令顺序，识别段顺序变化；提供接口、VLAN、无线、AP Group、管理地址、ACL、路由和常规分类。
- 原始配置参与变化判断，公开内容先脱敏；凭据变化、整段私钥变化以隐藏变化提示表示。整段私钥在配置段拆分之前脱敏，避免含分隔符的多行私钥片段进入公开报告。
- LCS 总预算 1,048,576 cells；每段最多预览 512 行、每份报告最多预览 2048 行。超限采用保守增删计数并显式标注预览受限，不把截断数据当作完整预览。
- “读取当前配置并预览恢复”重新连接、采集和保存完整当前快照，以当前 → 所选历史的方向比较，并验证型号与软件版本兼容性。响应始终 `writeAllowed=false`，明确 `FULL_DEVICE_RESTORE_NOT_IMPLEMENTED`，版本不一致增加单独阻断原因。
- 设备切换会清除配置内容、比较结果、描述输入、选择与确认；基线切换撤销写入确认，旧设备的异步响应不会显示在新设备页面。

## 原生检查点状态机

`H3cCheckpointRestoreService.cs` 为授权 `192.168.56.3:22 SSH` SW 提供受控验证入口，不接受任意设备或任意外部检查点恢复。

步骤为：校验完整基线 → 锁定端点 → 当前完整采集及版本匹配 → 接口 / LLDP 关键探针 → 读取原生检查点列表 → 固定 `configuration commit` → 必须识别唯一新增 ID 且既有 ID 保留 → 检查原生 replace 交互实现能力 → 再次确认当前完整配置未变化 → 临时描述变更 → 全配置读取验证仅描述发生变化 → 恢复刚创建的精确检查点 → 关闭旧连接并重新连接 → 完整配置哈希 / 型号 / 软件版本验证 → 独立接口和 LLDP 探针比对。

任何不确定写入进入独立、有界恢复尝试；记录精确检查点 ID、before / changed / restored / rollback 完整备份和以下分离的证据：

- `CONFIG_WRITE_SUCCEEDED`：受控描述命令返回成功。
- `CONFIG_READBACK_VERIFIED`：原生恢复后重新连接，完整配置与版本匹配原基线。
- `SERVICE_VERIFIED`：在完整配置匹配之外，接口链路 / 协议及 LLDP 邻居均匹配原基线。
- `ROLLBACK_VERIFIED`：失败后单独恢复、重新连接、完整配置与关键探针均验证成功。

`SERVICE_VERIFIED` 的范围明确为 `INTERFACE_LINK_PROTOCOL_AND_LLDP_NEIGHBORS`，不代表终端流量、应用业务、无线 HA 或跨设备端到端业务验收。未知 / 空且不可识别的探针会阻断验证，不将空数据当正常。

真实 session 的 `SupportsNativeCheckpointRestore=false`。原生 replace 确认交互没有实际证据，transport 明确阻断 `CONFIG_CHECKPOINT_REPLACE_INTERACTION_UNVERIFIED`；即使将来 commit 意外创建 ID，也会在描述变更前阻断。测试注入 session 才能执行替换分支，不能把注入测试当真实设备恢复通过。

Netmiko Worker 的只读命令响应明确保留原始命令 echo 和设备提示符（`strip_command=false` / `strip_prompt=false`）。真实检查点列表输出为固定命令 echo、`No commit point exists.` 和 `<sw1>`。检查点解析器从已经验证的完整当前配置提取唯一 `sysname`，仅剥离一次边界上的固定 `display configuration commit list` echo，以及与该 `sysname` 精确匹配的 `<sysname>` / `[sysname]` 提示符；同时支持提示符与命令同行或分行的明确 echo 形式。其他命令、未知提示符、重复 echo、未知控制字符和表格中的额外错误行均保持硬失败，不能通过任意删行来获得“无检查点”或有效 ID。

## 真实 SW 证据

证据位于 Next Workspace：

- `reports/r70-device-20261107-233152`：支持 `configuration commit`、`configuration replace commit-id/file/label/last` 命令树，初始 `display configuration commit list` 为 `No commit point exists.`。
- `reports/r70-device-20261107-233857`：commit 创建帮助仅有 `delay`、`max-number`、`<cr>`，未广告创建 label 语法；replace commit-id 在无检查点时不能给出现存 ID。
- `reports/r70-sw-checkpoint-20261107-234349`：真实执行裸 `configuration commit`，只返回 `[sw1]` 提示符；随后 list / verbose 仍显示 `No commit point exists.`。没有可验证的新检查点，未执行 replace、接口描述写入或其他运行配置变更。
- `reports/r70-sw-checkpoint-20261107-234525`：真实接口 brief 和 LLDP 命令均支持；邻居为 GE1/0/1 → AC1、GE1/0/2 → FIT-AP、GE1/0/3 → AC2。接口图例中的脱敏标记是证据脱敏程序的字串替换，不影响接口表格。

上述真实 SW 探查前后 canonical 完整配置 SHA256 均为：

`0c1f167f14300ea6ad2262e23596404b0fa905fc1f88ecc853be2e2f28171161`

服务专门返回 `NATIVE_CHECKPOINT_NOT_CREATED`，不把 CLI 无报错视为创建成功；此条件下不调用描述 Writer 或 native replace。

2026-11-08 根集成通过真实 Service HTTP 入口再次执行受控检查点验证，报告为 `reports/r70-configuration-http-20261108-000030.json`，operation 为 `4674ba41cc7d4474b6dc5038a9c55f8f`。结果为 `PASS_NATIVE_RESTORE_BLOCKED`：返回 `NATIVE_CHECKPOINT_NOT_CREATED`，`BeforeBackupId` 可追溯，四项独立验证 flags 均为 false；操作前后 canonical SHA256 仍与上文相同。此结果验证 HTTP 路由、证据和硬阻断闭环，不代表配置写入或原生恢复成功。

## 定向验证

- `--complete-config-diff-tests`：10/10 PASS。覆盖分类上下文、仅凭据变化、多行私钥及分隔符、重复命令、资源上限、行尾噪音 / 配置段顺序、历史比较、损坏源阻断、实时只读预览和版本不兼容。
- `SimulatorConfiguration.test.ts`：10/10 PASS。覆盖显式确认、设备切换、无效基线、已选基线身份失效后撤销、历史选择与分类、只读预览、比较错误和过期响应、脱敏下载，以及检查点独立确认 / 证据展示。
- Web typecheck：PASS。
- `--checkpoint-restore-tests`：14/14 PASS。前 10 项由根集成流程的完整 Service harness 执行并回传结果；真实 echo / prompt 边界修复后，再次定向 build + run 14 项全部通过（EXIT 0）。覆盖成功路径、不确定写入恢复、不确定替换恢复、回滚失败、完整哈希相同但服务漂移、未知 / 歧义检查点、缺失关键探针、基线漂移、实际同型 commit 无效果、未验证 replace 能力阻断、真实 echo / prompt 形态下的无新增检查点与表格读取，以及未知 transcript 内容拒绝。
- 根集成 C# build：0 warnings / 0 errors。检查点 fixture 通过不代表真实原生恢复通过。

根集成流程负责最终 Release build、完整 Service harness 和浏览器操作；本报告仅记录已回传的真实 HTTP 路由验收结果。

## 剩余闭环

1. 需要能实际创建可读取检查点、可验证 replace 交互的授权设备 / 模拟器，实现并验证精确原生替换。当前 SW 广告命令但不产生检查点，不能重复包装为设备互操作成功。
2. 任意本地历史完整配置仍需要设备支持的完整传输 / staging / replace 能力、型号版本兼容检查及完整恢复验收；不可通过逐行合并伪装整机恢复。
3. 完整私有备份目前仍为受控单设备采集；现有脱敏备份批量 / Site / Domain / 定时能力不能视为完整私有原件相同能力。完整原件批量和定时调度仍需独立闭环。
4. 未执行任何 Production 或旧 NetConsole 开发数据根访问；未复制完整真实数据根。测试使用 Next Workspace 内最小隔离 fixture。
