# R7.1 LLDP 真实采集与解析修复

日期：2026-11-08。状态：定向代码、完整 Service 集成、真实产品重采与浏览器验收 PASS；全产品仍为 PARTIAL。

## 实际范围与基线

- 仅 Next 隔离数据根 `D:\study\NetConsole-Next-Workspace\test-data\simulator-live-20261105`。
- SW `192.168.56.3:22`，精确库存关联 profile/device `c18dcb225dda4d059ce9799a71a28b2b`，Site `local`；库存与来源 Site 相同。
- 通过产品 API 的既有有界只读任务执行 `display lldp neighbor-information list` 和 `display lldp neighbor-information verbose`，不读取设备凭据、不直接登录、不改库存、不写设备。
- 采集前该 profile 的数据库 facts 总数为 0，API LLDP facts/claims 也为 0。因此本 profile 的零关系来自此前没有产品发布证据；不能推断其余 14 个 profile 的具体原因。

## 真实 Raw 与发现

任务 `5952c0987d2248c199e058f19f6d35ef`，定义 `r71-lldp-f521d0bfa95a44d8884540ceabe8358c`，2026-11-08 01:40:48—01:40:51 CST。终态 SUCCEEDED，两命令 COMPLETE/PARSED。

| 命令 | 输出字节 | 输出 SHA-256 | 分段 SHA-256 |
| --- | ---: | --- | --- |
| list | 498 | `4605da47b59ff376dc7e004fbd8c95bee9e05974dc1b935cc294e2e5db0a93bf` | `fce80a4ccb1507b4defff0c1ecbbb9fc0232120a42d7c6e2f775eb899d3a52ed` |
| verbose | 4506 | `1d27cc0435fd71471046ca7a1c1745d58e04a82fb91dcfd97d144a10ccfce5a5` | `1da278896dbd48a8d8fca659c299b9221701cf1d3032daed48421a3687accfff` |

现场 list 表头为 `Local Interface / Chassis ID / Port ID / System Name`。旧解析器未识别该布局，把表头前 Chassis/Default 说明行误当邻居，并把 chassis 误置 sysname、sysname 误置 management address，发布 5 条含错误语义的事实。任务/Raw 成功不能证明解析或拓扑业务成功。

初版探针只校验元数据，曾给出 PASS；复核后报告保留 initialMetadataValidation，并将总体标为 PARTIAL、semanticValidation 标为 FAIL。证据：`D:\study\NetConsole-Next-Workspace\reports\r71-product-lldp-20261108-014048\summary.json`。未删改 Raw 或事实历史。

实录正确关系为：

| SW 本地接口 | 邻居 System Name | Chassis ID | 邻居接口 |
| --- | --- | --- | --- |
| GE1/0/1 | AC1 | 4c48-8d10-0100 | GigabitEthernet1/0/1 |
| GE1/0/2 | 4c48-cb6e-0400 | 4c48-cb6e-0400 | GigabitEthernet0/0/0 |
| GE1/0/3 | ac2 | 1cc0-ab25-0600 | GigabitEthernet1/0/0 |

list 没有管理地址，故关系的 management address 必须为 null。没有根据名字把邻居绑定到其他 Site 库存，也没有将第二个邻居推断为有确定业务身份的 AP。

## 最小修复

- `Phase9VendorParsers.cs`：按明确表头区分已有 system-name-first、已有合成 neighbor 列及真实 local-interface-first 布局；跳过表头前说明；部分、分页、未知或错误行不构成完整集合。
- `H3cComware.cs`：沿用解析结果数组形态，完整空表允许 PARSED，失败状态明确返回。
- `H3cFacts.cs`、`H3cLldpListTable.cs`：仅带产品 Raw provenance 的完整 LLDP list 发布 `lldp.table` 集合 revision。完整空表也有 revision。新增的 table 退役操作仅匹配同 profile、完全相同 captured scope、精确 `H3C_TASK_RAW:` 来源、较旧捕获时刻的 LLDP 邻居 Current；该操作不会匹配其他来源/范围/事实种类，不删除历史，不产生 REMOVED。同范围采集证据的更老首次发布或重放不能因本次集合更新复活 Current。表元数据不是 `lldp.neighbor`，不进入关系投影。
- 实录 fixture `tests/NetConsole.Next.Service.Tests/fixtures/h3c/r71_simulator_sw_lldp_list.txt` 取自以上已校验 Raw 输出（仅清理行尾空白），仅含白名单模拟器 LLDP 内容，无凭据。
- 探针 `tests/r71_product_lldp_probe.py` 补充精确三关系语义、管理地址为空、scope/source、事实 Raw verified，以及两段输出/分段 SHA 和字节数验证。

边界：上述过滤只约束新增的 table 退役及采集集合排序操作。既有通用事实 Current 继承仍以 `profile_id + kind + identity_key` 为键，没有把 source 或 captured scope 纳入键；不同来源/范围若使用同一 identity，较新事实仍可能继承并替换该键的 Current。本批没有实现跨来源/范围的独立 Current authority。测试中用于验证过滤保留的其他来源/范围行均使用不同 identity，不能据此宣称同 identity 隔离。

## 验证与后续

- LLDP topology：5/5 PASS，包含真实 SW 三邻居列映射、说明行过滤及 Phase11 旧实录兼容。
- Phase11 离线 facts 定向回放：PASS（facts=28、peers=3）。旧 AP-229 实录有版权 banner、命令 echo、完整表行但没有 closing prompt；普通 `ParseLldpList`（也被既有 CriticalServices 调用）保留既有表抽取契约，严格产品 `ParseLldpListTable` 仍返回 PARTIAL，不授权 Current 集合替换。新增回归直接使用原 fixture，保留原 count/local/remote 断言并验证 sysname、chassis 及离线关系投影。
- 产品事实发布：17/17 PASS，包含完整集合收缩、完整空表、partial/unknown/pager 不替换、同 revision/旧 revision 重放、已保存 Raw 的延迟首次发布、table 退役过滤保留不同 identity 的其他来源/scope 行及其他 kind、历史保持。未验证或实现同 identity 的跨来源/scope Current 隔离。
- `git diff --check` PASS。
- 子任务没有重启 Service、重建运行目录、再次连接设备、提交或 push。

## 最终产品集成与浏览器验收

- 最终完整 Service harness `reports/r71-service-harness-closure-final.log` EXIT0；`r71-harness-closure-build.log` 与运行构建 `r71-service-closure-build.log` 均0 warnings/errors。此前 QuickPing 发布竞争和 Phase11 兼容失败日志均保留，修复后原断言通过。
- 新 Service PID50452 在同一独立数据根运行。2026-11-08 02:02:54—02:02:56 CST 产品任务 `74a3f409205646d5801b23776023469e` 两命令 COMPLETE/PARSED，输出及分段哈希验证通过，semanticValidation/validation 均 PASS。报告：Workspace `reports/r71-product-lldp-20261108-020254/summary.json`。
- 产品集合发布将旧5条错误 Current 退役，保留原事实历史，新的 Current 为上表正确3条；未手工删除或修改事实、Raw，不写设备，不读取设备凭据。list/verbose 输出SHA与首次实录一致，新分段SHA分别为 `0d2e8437ee978750bfd8c31a5a4145c4b1b677e5daa1524abc531c264ccd0ad1`、`b3bd45e01aedefa3a5d5ebd1b5750e6772bd73d7914599e81699757eb2a644f1`。
- 浏览器直接访问 Service18771 已构建UI：加载3条关系，精确 profile + GE1/0/2 + Site local 筛选得到1条；CSV820bytes/1数据行，source/factRevision/relationKey 与API逐项核对。API/page/console错误0，浏览器无额外设备连接。`reports/r71-browser-lldp-closure-20261108/result.json`。
- 1920/2560两张截图已逐张目视，无溢出、重叠。不能据此扩大为全站身份消歧、同identity跨来源Current隔离或完整图形拓扑验收。
