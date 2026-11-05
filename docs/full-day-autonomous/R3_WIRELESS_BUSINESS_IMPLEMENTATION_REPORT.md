# R3 无线业务实现报告

状态：PARTIAL。既有 AC/AP projection、Observation、Current/Recent10、Site scope、collection manifest read API/UI 与离线 fixture 保留；正式 AP identity lifecycle、AP Group 完整生命周期和双 AC HA 仍需真实认证与独立合同。不得把离线 fixture 写成模拟器或生产证据。

本批次新增：

- `GET /api/system/wireless/ac-domains/{id}/collection-manifests`
- `GET /api/system/wireless/collection-manifests/{id}`
- 无线页面“采集记录”列表和详情，展示 source AC、source revision、Raw manifest reference 与当前可见 Observation。
- 列表和详情都按当前 Site claims 过滤；source AC 过滤后的 manifest 元数据由可见 item 重新聚合。
