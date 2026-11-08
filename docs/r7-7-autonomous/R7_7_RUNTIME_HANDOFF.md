# R7.7 运行交接

日期2026-11-08；Agent codex-A；Thread01a1ba51-04f2-71a2-ac64-17bb40048093。
分支codex-A/netconsole-next-r3-dual-ac；产品提交至1cd8996，验收工具bb84c9f。文档检查点提交可用git log检索“保存R7.7最终集成验收与运行交接”。仅本地提交；禁止Push、远端合并、发布。

## 当前运行

- Service：http://127.0.0.1:18771，PID21540，显式dotnet启动，HEALTHY。
- Candidate：D:\study\NetConsole-Next-Workspace\build\r77-candidate-api。
- Worker：候选tools/netmiko/netmiko_worker.py，含2ad4698；固定离线依赖18项。
- Vite：http://127.0.0.1:5174，PID32812；未停止/替换。
- 数据根：D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105。
- Syslog端口15514。共享Service全局mutex：不要与独立Service测试同时启动。
- 生命周期：tests/r70_runtime_control.py；只操作精确PID/DLL，不结束共享Console。

## 数据与检查点

r77-pre-upgrade-integrity.json：152 FINALIZED，SQLite ok，Raw长度/hash逐项通过。
r77-post-upgrade-integrity.json：164 FINALIZED，新增12来自双AC只读命令，incomplete0；原R7.6单库与19资产不变；恢复issues为空。

升级前单一R7.7检查点：test-data/r77-runtime-checkpoint-20261108/catalog.db；
SHA256 7f7c35efd69fbf919a78630e3a16ef98913f392241eb29c72e646648b6edccf8。
保留原R7.6检查点：test-data/r76-runtime-checkpoint-20261108/catalog.db；
SHA256 5fba203ba26a99caa2f9c05229646415f0e9bd1b64b048975f352e180098173f。
不复制完整数据根，不访问旧NetConsole Production/Dev数据。

## 复验和接续

最终候选显式DLL完整离线EXIT0；APPHOST完整离线EXIT1/ICMP_PERMISSION_DENIED。不能以窄探针替代，也不能把DLL通过推广到SCM。
双AC最新报告reports/r72-product-dual-ac-20261108-233229/summary.json。无SW/FIT认证重试、无配置写入、无HA。
共享专项浏览器10项含2 PARTIAL；广覆盖4项PASS；详见UI报告。
300.006秒短时运行观察已完成，21次采样全部HEALTHY、同一PID21540；设备连接/任务准入0。UTC15:34:41–15:39:41，输出reports/r77-runtime-observation-300s.json。8–24h验收仍NOT_TESTED。

观察结束后UTC15:39:47，reports/r77-final-runtime-integrity.json再次确认SQLite ok、164 FINALIZED、incomplete0、逐Raw长度/hash与19资产/原检查点不变；R7.7检查点SHA256单独复核一致。Vite PID32812与Service PID21540均存活。测试脚本Python语法/Node语法和git diff --check通过。

继续入口：[最终验收](R7_7_FINAL_ACCEPTANCE.md)、[剩余缺口](R7_7_REMAINING_GAPS.md)、[任务池](R7_7_TASKPOOL.md)。保留既有未跟踪19静态资源和其他worktree；精确暂存，不git add -A，不push。
