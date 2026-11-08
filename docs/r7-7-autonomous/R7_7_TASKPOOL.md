# R7.7 工作流任务池

2026-11-08；Agent codex-A；branch `codex-A/netconsole-next-r3-dual-ac`。基线 b4d942e；产品提交至1cd8996。仅本地提交，禁止 push/remote merge/release publish。总体 PARTIAL；下表记录最终集成结果，各工作流报告中的早期记录仅用于追溯。

| 流 | 代码范围与当前批次 | 当前验证 | 剩余与下一批 |
| --- | --- | --- | --- |
| A 设备 | Comware SSH/Telnet完整提示符读取，2ad4698 | Worker57/57；18固定依赖bundle smoke；候选双AC各6命令及12 Raw校验 PASS | SW无新有效凭据不重试；其他型号继续逐项验收 |
| B 无线 | 冻结R3 Domain/Resource/Observation/ONE_PLUS_ONE保持复用 | 双AC产品SSH任务成功；同一AP Resource、AC1 R/M与AC2 R/B独立来源/时间/Raw PASS | 实际有效变化历史为空，浏览器2项PARTIAL；HA/客户端连续性单独验收 |
| C FIT | 复用Telnet Worker与完整提示符校验 | Worker双传输回归 PASS；原生认证 BLOCKED | 有效凭据、exec-console实际读回、原生MAC/Serial与AC关联；不猜密码 |
| D Ping | 连续3个有效丢包确认故障、3个新回复证明恢复，b9d27de | 健康23/23、LongPing control3/3、authority7/7、QuickPing9/9；HTTP13/lossHTTP18/browser18 PASS | 活动Long Ping事件投影、真实完成历史、权威客户端/AP关联 |
| E 故障 | 同资源新恢复证据与UI解释 | 故障27/27、前端16/16和完整Service套件 PASS | 协议并行、活动任务复发、Syslog/配置事件及真实故障恢复 |
| F 配置 | CompleteConfigurationDrift + API + UI，dd72333 | 比较10/10、配置26/26；最终候选HTTP14/browser16 PASS | 自动变化告警；完整设备恢复/失败回滚BLOCKED，不能用Writer成功替代 |
| G 迁移 | 复用FEATURE_MIGRATION_MATRIX和终端/诊断/配置/导出 | 显式DLL完整离线门禁覆盖Linux终端、配置类型及完整诊断 PASS | Mesh/专业报表/SSH中转按既有矩阵继续切片，完整迁移PARTIAL |
| H 部署 | ICMP诊断cfc801c、API错误边界1cd8996、受控候选升级 | 显式DLL完整离线 EXIT0；API权限43 checks；单库检查点和升级后完整性 PASS | SCM服务账户/安装/升级/回滚/卸载未测；不改全局安全策略 |
| I 路由 | 同进程原生ICMP与生产tracert诊断 | 专项4/4；最终显式DLL完整离线真实Windows回环ICMP PASS | AppHost完整离线 EXIT1/ICMP_PERMISSION_DENIED，PARTIAL；TCP可达不冒充TTL路径 |
| J UI 性能 | 配置变化/精确差异、Ping故障说明、未知API JSON错误 | Web36 files/393 tests、typecheck；配置浏览器16；共享广覆盖4项/8布局PASS，专项8 PASS/2 PARTIAL | 首屏与大数据性能、实际变化/完成Long Ping导出；1920/2560已测，125%不在范围 |

## 集成与运行

集成负责人协调Program.cs、共享API、候选生命周期与设备操作；空白agent回复未用作验收证据。五个独立产品提交：2ad4698、dd72333、b9d27de、cfc801c、1cd8996。测试脚本和验收文档另作本地检查点。

共享Service从PID41856受控升级至18771/PID21540，显式dotnet加载build/r77-candidate-api；Vite5174/PID32812保留。候选构建于1cd8996提交前，包含该代码但元数据不是干净1cd8996发布。数据仅为Next test-data/simulator-live-20261105。升级前152 Raw，双AC只读新增12，最终164 FINALIZED；SQLite ok、incomplete0、原R7.6检查点与19资产不变。完整数据根未复制，设备配置写入0，无HA操作。

短时观察reports/r77-runtime-observation-300s.json已完成：300.006秒、21采样全部HEALTHY，PID21540不变；观察后完整性再次PASS。8–24h LONG_RUNNING仍NOT_TESTED。共享全局mutex存在，隔离Service测试不得与共享Service同时启动。

## 下一批顺序

1. 根据实际进程上下文继续定位AppHost ICMP阻断；显式DLL完整门禁与AppHost失败分别记录。
2. 继续活动Long Ping的有序样本故障投影与配置变化自动事件，复用资源、Site、有效样本和恢复证据边界。
3. 新有效凭据到位后执行SW/FIT单次定向认证；完整备份、读回、回滚及业务证据齐备后再做恢复/HA。
4. 独立Windows SCM服务账户验收和实际8–24h运行窗口，不能用300秒观察替代。

详细边界：[剩余缺口](R7_7_REMAINING_GAPS.md)、[最终验收](R7_7_FINAL_ACCEPTANCE.md)、[运行交接](R7_7_RUNTIME_HANDOFF.md)。
