# 原版 NetConsole 功能迁移状态

原版只读盘点证据与 Next 映射见 `../r6-feature-parity/`。本轮实际代码交付为：

- 智能配置 dry-run：采用 Next 的模板、Site scope 和权限边界，不复制原版架构。
- 拓扑/资产工作台：复用 Next 设备库存、H3C LLDP claims 和事实来源，不根据名称/IP 推断关系。
- 配置恢复 UI：复用现有预览、确认 token 和 fail-closed writer，不把安全拒绝伪装为完成。

整体对标仍为 `PARTIAL`；原版的成熟命令兼容、报表、批量操作和历史行为以源码证据为准，真实设备和完整页面闭环仍有差距。
