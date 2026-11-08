# R7.3 双AC与FIT

2026-11-08，codex-A。双AC实际切换/回切及无线业务恢复 **NOT_TESTED**，FIT原生认证 **BLOCKED**。

## 已有模型与本轮改进

沿用冻结R3的Domain、Member、AP Resource、独立Observation、完整Serial/MAC、Current/PreviousEffective/Recent10。没有按AP名称合并身份，没有以采集时间差推断HA切换。

本轮无线页增加当前筛选AP Observation CSV。一行一个来源观测，保留Site、Domain、AC、物理资源ID、Serial/MAC、原始/规范状态、Raw引用、source/projection revision、筛选条件、部分加载标记和导出时间。一个物理AP的AC1/AC2观测保留为独立行，按当前Domain/AC精确筛选。导出仅覆盖已经加载的授权Observation。

修复刷新失败后旧AP资源残留问题。刷新开始清空旧Domain/AP集合，资源API失败后不继续显示或导出先前授权上下文的数据。新增正常工具栏导出与刷新失败回归。

## 当前设备证据

四个授权端点本轮各一次TCP检查均超时。Host-Only接口/路由正常，但AC1/AC2/SW邻居解析失败，未发现运行中的HCL/虚拟机实例，详见设备互操作专项。本次没有新AC身份、AP归属或HA运行事实。

R7.2的R/M、R/B及同一Serial/MAC双源观测继续保留为历史证据。Radio Down、客户端0的旧观测不能证明业务恢复。没有可靠独立管理和精确回切路径的新证据，未关闭SW GE1/0/1、未调整主备或故障注入。

FIT只有既有AC侧身份信息，缺适用的有效原生密码。本轮未猜密、未重复被拒凭据、未建立FIT独立无线管理身份，原生Version/Interface/MAC/Serial/Raw仍待。

## 定向测试

无线导出4项、WirelessView20项、Dashboard3项通过。导出测试覆盖双源保留、精确Domain/AC、STALE/CONFLICT及CSV公式/引号/换行。浏览器和完整无线Service回归结果统一列于最终验收。
