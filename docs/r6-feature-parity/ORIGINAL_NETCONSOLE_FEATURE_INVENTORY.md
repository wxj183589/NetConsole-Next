# 原版 NetConsole 功能盘点

日期：2026-11-07。范围：只读源码审计；未启动旧版服务，未读取或修改旧版数据库和真实数据。

| 领域 | 原版真实入口/实现证据 | 行为摘要 | 优先级 |
| --- | --- | --- | --- |
| 设备管理与连接 | `src/netconsole/backend/api/device_management_router.py`、`services/device_management_web_service.py`、`services/connection_manager.py` | 设备、分组、连接测试、批量导入、站点范围和审计 | P0 |
| AC/AP 无线 | `backend/api/ac_management_router.py`、`ac_mesh_link_router.py`、`services/ac/`、`services/ap_identity/` | AC 资源、FIT-AP、Mesh、光衰、身份和导出 | P0 |
| 配置采集 | `backend/api/config_collection_router.py`、`services/config_lifecycle_service.py` | 快照、比较、预览、保存确认、导出和删除 | P0 |
| 任务中心 | `backend/api/job_center_router.py`、`task_router.py`、`services/background_job.py` | 后台任务、取消、重试、历史、结果和审计 | P0 |
| 终端/中转 | `services/site_ssh_relay.py`、`external-terminal/`、`file_transfer_service.py` | SSH/Telnet、Linux、跳板和只读文件传输 | P0 |
| 网络工具 | `backend/api/network_tools_router.py`、`services/network_tools/` | Ping、TCP、路由、fping/iPerf 和结果记录 | P1 |
| LLDP/拓扑 | `services/ap_topology/`、`repositories/mesh_catalog_repository.py` | 邻居、Mesh 关系、拓扑和诊断 | P1 |
| 轨道业务 | `backend/api/rail_transit_base_data_router.py`、`trackside_ap_business_router.py` | 局点、轨旁 AP、车载通信和基础资料 | P2 |
| 文件/报表 | `backend/api/file_management_router.py`、`export_worker.py` | 文件浏览、远程下载和报表导出 | P1 |
| 系统治理 | `feature_registry.py`、`backend/api/system_settings_router.py`、审计仓储 | Feature 开关、权限、日志和系统设置 | P1 |

已证实的 H3C 只读命令集中在 `adapters/h3c/h3c_command_profile.py` 和 `services/command_guard.py`，包括 `display version`、接口、LLDP、WLAN AP、Mesh 相关显示命令和 `display current-configuration`。命令清单不代表每个现场型号都已互操作通过。

