# R7.2 运行交接

2026-11-08，codex-A，接续 `065a749` 及其既有R7.2未提交工作。

## 当前入口

- 集成Service与内置正式构建Web可访问 <http://127.0.0.1:18771>。
- 开发Vite入口可访问 <http://127.0.0.1:5174>。
- 当前交互Service采样PID `32828`，HEALTHY。PID会随重启变化，以health中的processId为准。
- DLL位于 Next Workspace `build/r72-service/NetConsole.Next.Service.dll`，SHA256见 `reports/r72-runtime-final.json`。
- 隔离数据根为 Next Workspace `test-data/simulator-live-20261105`，catalog schema27，SQLite integrity ok，90段历史命令Raw完整性验证通过。
- Syslog绑定本机测试端口15514。数据根含现有模拟器库存、用户和凭据引用，凭据不写入交接。

## 受控停启

```powershell
python tests/r70_runtime_control.py stop --pid <health中的PID> --expected-dll D:/study/NetConsole-Next-Workspace/build/r72-service/NetConsole.Next.Service.dll --report-prefix r72
python tests/r70_runtime_control.py start --expected-dll D:/study/NetConsole-Next-Workspace/build/r72-service/NetConsole.Next.Service.dll --report-prefix r72 --syslog-port 15514
```

先停服再覆盖运行DLL。控制脚本核验PID、命令行和独占控制台，受控退出失败时保留进程并调查。LocalManager交互运行和实际SCM验收分别记录，详见部署专项。

## 接续边界

现有AC1/SW本次只读采集超时，全部三个白名单SSH端口均有界超时。FIT缺有效认证材料，HA独立回退未核实，SCM创建权限拒绝。历史成功证据可在页面读取，不能用于声明当前设备在线或本次重采成功。配置整机恢复五项结果均未提升。

本轮保留前后端运行供验收。旧NetConsole真实数据根未访问。Git仅本地提交，DevStatus仅新增本任务交接文件，已有其他任务dirty不覆盖。最终提交和工作树状态见最终验收。

已运行的产品检查点为 `2157206`。静态Web10场景、HTTP35检查、90段Raw与SQLite验收结果见Workspace `reports/r72-runtime-final.json`、`reports/r72-browser-final-complete/result.json`。源码与运行DLL的行为对应产品检查点，后续提交仅验收文档和交接。
