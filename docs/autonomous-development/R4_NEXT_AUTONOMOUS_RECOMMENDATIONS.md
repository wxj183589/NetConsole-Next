# R4 Next Autonomous Recommendations

按价值和可验证性排序：

1. 将 identity JSON authority 迁移到正式 schema-backed authority，保留当前 evidence、Site、revision、audit 和 replacement scope gates。
2. 在取得安全凭据和基线/恢复证据后，为白名单模拟器实现独立 writer；继续保持 preview、dry-run、token 和回滚门槛。
3. 继续任务中心：把新任务 ID route、重试、取消、恢复和 Site scope 做成统一任务详情入口。
4. 若安全存储出现凭据，按 AC1、AC2、SW、FIT-AP 顺序执行只读基线，保存配置摘要和恢复检查点，再做受控 HA 测试。
5. 重新构建当前 HEAD 的隔离离线候选，记录 bundled runtime、静态 UI、Netmiko worker、数据根和目标机依赖。
6. 最后推进跨机 TLS、安装/SCM/升级/回滚和实际网络访问；不开放公网，不关闭证书校验。

每个推荐批次都必须产生代码、定向测试、提交和状态更新；外部阻断只阻断对应工作流，不阻断本地工作流。
