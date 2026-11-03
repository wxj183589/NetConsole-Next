# R2 设备创建/编辑验收

## 已实现

- 创建设备继续使用稳定 ID（留空由服务端生成）和管理 IP；编辑时 ID 保持 disabled，不以地址代替身份。
- 厂商、型号和设备类型使用明确字段；厂商保留简单输入，类型改为受控选择。
- Site 由当前授权 scope 提供，表单中只读显示；分组只能从当前 Site 的真实 groups 选择。
- 终端档案明确标注为“认证引用”，只提交 `terminalProfileId`，不增加密码输入，不把秘密放入 localStorage、URL 或设备 inventory。
- FIT-AP 只作为已有 inventory 的可见类型；新建设备没有 FIT_AP 选项，保存函数仍有第二道 UI guard，并提示“必须由 AC 管理”。
- 保存、刷新、批量写入的现有 SUPERADMIN、CSRF、审计和原子确认链路不改。

## 视觉证据

- `D:\study\NetConsole-Next-Workspace\build\ui-r2-device-management\final\device-create-dialog-1920x1080.png`

## 边界

本轮没有新增 AC/FIT-AP 领域模型、credential vault、迁移或真实设备凭据流；UI 只引用已有服务端 profile reference。直接绕过 UI 的 API 语义仍由既有服务端契约和后续 AC/FIT-AP domain work 管理。

