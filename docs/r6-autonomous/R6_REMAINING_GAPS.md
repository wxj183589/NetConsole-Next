# R6 剩余缺口

- 真实 H3C/ZTE/Linux/跳板认证、命令互操作和四台授权模拟器的凭据仍不可用。
- 双 AC 1+1 的配置角色、运行角色、HA 切换、恢复和 AP ownership 没有真实前后状态证据。
- 配置恢复默认 writer 仍 fail-closed；不得以 dry-run 宣称恢复完成。
- AP identity 当前隔离 authority 尚未完成 schema-backed 并发 mutation、merge/split 和 replacement revoke。
- 智能配置目前只到模板变量校验和命令 dry-run；没有设备适配、批量执行、差异验证或回滚。
- 拓扑/资产已提供库存与 LLDP claims 只读表格，但没有图形拓扑、资产关联历史和跨来源合并。
- ARP/路由/WLAN/Mesh 诊断尚无完整用户闭环。
- 跨机器 TLS、Windows Service 安装、升级/回滚和原生 125% 仍属外部验收。
- 当前 workspace 规则引用的检查脚本缺失，需后续由仓库维护者补齐或更新 canonical 文档。
