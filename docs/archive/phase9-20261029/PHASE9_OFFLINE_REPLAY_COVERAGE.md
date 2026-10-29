# Phase 9 离线 replay coverage

## C# 回归

命令：`dotnet run --project tests/NetConsole.Next.Service.Tests/NetConsole.Next.Service.Tests.csproj -c Release`

结果：`PHASE9_VENDOR_PARSER_REPLAY=PASS`，随后完整 harness 通过；本轮新增角括号提示符和正文重复命令回归后仍通过。覆盖正常、空、未知、分页、权限、不支持、编码、输出上限、取消、断线、重复记录、H3C AP/Radio/LLDP/光模块及 ZTE 五条命令。

## 黄金样本

| fixture | SHA-256 |
| --- | --- |
| H3C `phase9_lldp_list.txt` | `bfe329b132585101f53d23ca64e5de58d18d07b000a1063145322397f02c4ea3` |
| H3C `phase9_transceiver.txt` | `21922ae550b29580f18d31d0c02e0525b472ed877f678a43838e480e553f338e` |
| ZTE `phase9_c89e4_show_version.txt` | `3fc0c87a2cb4f47f332cbb24f050931be6103dd506e1366c4c6fe623977a0da5` |
| ZTE `phase9_c89e4_show_interface_brief.txt` | `7a67d67cfcef0d8972d16fad4f48a85ae8dd31a85f031a6476201e7ca237dfda` |
| ZTE `phase9_c89e4_show_opticalinfo_brief.txt` | `995e81b326c78792b307f14cb2b162f5c889461d906f9f42c543ea05aac25ed9` |

原项目真实开发 Authority 中仅做只读摘要核对，未将原始日志复制入 Next。`OLD_RAW_OUTPUT`/`OLD_PARSED_RESULT` 的可追溯来源由原仓库 fixture、原项目 parser 测试和 Dev Authority 文件 SHA 组成；提交仓库的黄金样本只保留脱敏副本。

## 覆盖结论

离线 parser/回显合同可长期回放；真实设备连接、Next ZTE runtime、领域存储/API/GUI 和生产 Release gate 仍未完成。
