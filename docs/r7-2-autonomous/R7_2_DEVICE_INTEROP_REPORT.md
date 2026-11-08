# R7.2 设备互操作实证

2026-11-08；codex-A；branch `codex-A/netconsole-next-r3-dual-ac`。本专项状态 **PARTIAL**：AC1/AC2/SW 的本轮产品只读采集通过；FIT 原生认证、配置恢复及 HA 切换不能据此宣称通过。

## 真实产品路径

复用隔离根 `D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105` 的库存、现有凭据引用和本机 API。探针不读取设备密码、不直接创建设备 transport。按设备串行运行，上一任务明确终态后才启动下一台；入队或终态不明即停止，不重试。

运行 `tests/r72_product_dual_ac_probe.py`：每台固定执行 version、ac-role、AP all/address/radio/verbose 共六条只读命令。

| 设备 | run ID | 结果 |
| --- | --- | --- |
| AC1 `.2:22` | `e7811304d2ff4f1d836e8f9542fd89c3` | SUCCEEDED，6/6 COMPLETE/PARSED |
| AC2 `.5:22` | `f5fe079e1f404b72b7363a330aa31cd2` | SUCCEEDED，6/6 COMPLETE/PARSED |
| SW `.3:22` | `54f54ae7ebca476eb13df38674a6b6b8` | SUCCEEDED，LLDP list/verbose 2/2 COMPLETE/PARSED |

证据位于 Workspace `reports\r72-product-dual-ac-20261108-033929\summary.json`、`reports\r72-product-lldp-scope-20261108-034026\summary.json`。逐段核对注册 Raw 的 segment SHA256、输出 SHA256、字节数、命令、run/profile 与 COMPLETE；双 AC 12/12、SW 2/2 通过。SW 的三个关系精确保持本地接口、远端端口及 chassis：GE1/0/1→AC1，GE1/0/2→AP，GE1/0/3→AC2；管理地址未知仍为 null。

## 实际失败驱动修复

首轮 AC1 run `9eaaf5a078524772b49934cb86e645f0` 的六段 Raw 完整，但 Radio 命令解析为 UNRECOGNIZED；该轮为 PARTIAL，失败证据保留于 `reports\r72-product-dual-ac-20261108-033020\summary.json`。

实际 H3C 表为 `AP name RID State Channel BW Usage TxPower Clients` 八列，原 parser 按旧五列解释，无法解析 State 与 `(auto)`。修复后严格识别八列表头、状态、正数信道/带宽、用量范围与非负客户端，同时保留旧五列格式，拒绝分页、拒绝访问和命令边界不匹配。新增带来源/捕获范围的 `ap.radio` 事实及 H3C 页面，显式保留 Down 与客户端 0。实际回采两台各两条 Radio，状态均 Down，信道 36(auto)/11(auto)、带宽 80/20MHz、功率 20dBm、客户端均 0。

## FIT 与验收边界

`.4:23` 的 FIT 仅有已知用户名，缺少新的有效密码；既有认证拒绝不能因 TCP 可达改记成功，本轮未猜测、未重复认证。AP 经 AC 的清单/地址/verbose/Radio 只读观测已通过，不等同 FIT 原生 SSH/Telnet 认证。

本轮未进行设备配置写入、端口关闭、AP reboot/reset、HA 切换/回切、客户端流量或无线业务恢复测试；旧 `D:\NetConsoleData*` 数据根未访问。

## 定向验证

- Scope 10/10、事实发布 18/18、LLDP/真实 Radio fixture 6/6 PASS；旧 Radio 五列保持兼容。
- Wireless runtime 14/14，group contract/projection、cache 4/4、replacement 3/3、identity contract 3/3 PASS。
- H3C 29、Topology 8、CSV 7，共 44/44 Web 测试 PASS；typecheck PASS。
- 设备详情邻居组件 5、DevicesView 13，共 18/18 PASS；刷新/失败、精确范围、迟到响应与导航均覆盖。
- 完整集成、浏览器及最终构建结果见主集成报告；此处不将组件测试升级为浏览器验收。

## 下午接续采集

本次接续实际HEAD `065a749`，产品检查点 `2157206`。AC1 run `e8443c6c7d354e10ad34912296990445` 与SW run `4ceb79ed60be4d2385db9e78dc3bbdbf` 均TIMED_OUT，未生成本次可验收新Raw。AC1未完成后双AC探针按约定停止，没有继续AC2设备命令。随后仅对三台白名单的22端口各做一次有界检查，全部TimeoutError，具体环境原因未定位。证据为Workspace `reports/r72-product-dual-ac-20261108-164308/summary.json` 与 `reports/r72-product-lldp-scope-20261108-164341/summary.json`。

此前凌晨成功保留为历史实证，本次重采为PARTIAL。隔离根的90段持久Raw在Service重启后逐段完整性通过，不能据此声明当前设备在线。本次没有FIT认证、配置写入、切换或回滚操作。
