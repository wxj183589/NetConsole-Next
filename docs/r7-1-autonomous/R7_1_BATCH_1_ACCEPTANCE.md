# R7.1 第一业务批次

日期：2026-11-08；起点 `bdcd4f9`；Agent：codex-A。整体验收仍为 **PARTIAL**。

交付实际产品代码：完整私有配置批量及计划类型、严格目标解析、重新连接回读校验；故障事件持久化及显式选择的补采策略；型号/版本/AC角色事实；Syslog接收状态、真实Raw检索与CSV；拓扑精确筛选与CSV；Windows服务安装计划、实例环境与部署文档。

验证：Service完整harness重跑 EXIT 0（`reports/r71-service-harness-batch1-rerun.log`）；Web 27文件291项PASS（`r71-web-batch3.log`）；typecheck与Vite通过；安装计划15/15、7个PowerShell脚本语法通过；Python Worker46/46。上述reports均位于Next Workspace。浏览器在1920及2560宽度验证配置、故障、Syslog、拓扑，未发现页面/API/控制台错误；不包含125%验收。

实际HTTP执行AC1、AC2、SW完整只读配置备份，逐个验证私有原文和公开脱敏视图哈希。Syslog实收一条明确标识的本地合成UDP，完成Raw→查询→CSV；不是设备Syslog互操作。拓扑15个profile claims查询成功但0条LLDP关系；有数据CSV仍缺实际存量证据。

原生完整恢复、手动HA切换和FIT直连认证仍BLOCKED，原因分别为未生成原生检查点、缺确证可恢复切换路径及无新有效凭据。未把已有描述写入器当作整机恢复；未关闭共享管理链路，未猜FIT密码。两台AC的Common角色及AP R/M、R/B均按实际观察记录。

这是阶段提交，后续继续实现计划立即执行、运维关联入口及部署/生命周期边界验证。尚未做最终运行环境交接。不Push、不Merge、不Release；未访问旧真实数据根。详情见同目录各专项报告。
