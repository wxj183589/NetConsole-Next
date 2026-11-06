<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import SiteSshJumpHostPanel from '../components/SiteSshJumpHostPanel.vue'
import { changePassword, getModuleStates, updateModuleState, type ModuleState } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore(); const saving = ref(false)
const modules = ref<ModuleState[]>([]); const modulesLoading = ref(false); const moduleUpdating = ref('')
const form = reactive({ currentPassword: '', newPassword: '', confirm: '' })
const managed = reactive({ username: 'admin', newPassword: '', confirm: '' })
async function save(): Promise<void> {
  if (form.newPassword.length < 8 || form.newPassword !== form.confirm) { ElMessage.warning('新密码至少 8 位，且两次输入必须一致'); return }
  saving.value = true
  try { await changePassword({ currentPassword: form.currentPassword, newPassword: form.newPassword }); ElMessage.success('密码已更新'); Object.assign(form, { currentPassword: '', newPassword: '', confirm: '' }) } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '密码更新失败') } finally { saving.value = false }
}
async function saveManaged(): Promise<void> {
  if (!auth.isSuperAdmin || managed.newPassword.length < 8 || managed.newPassword !== managed.confirm) { ElMessage.warning('仅 SUPERADMIN 可管理用户，且新密码至少 8 位并需确认一致'); return }
  saving.value = true
  try { await changePassword({ username: managed.username, newPassword: managed.newPassword }); ElMessage.success(`${managed.username} 的密码已更新`); Object.assign(managed, { newPassword: '', confirm: '' }) } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '用户密码更新失败') } finally { saving.value = false }
}
async function loadModules(): Promise<void> {
  modulesLoading.value = true
  try { modules.value = await getModuleStates() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '模块状态读取失败') } finally { modulesLoading.value = false }
}
async function toggleModule(module: ModuleState): Promise<void> {
  if (module.isCore && module.enabled) { ElMessage.info('核心模块不可停用'); return }
  moduleUpdating.value = module.key
  try { const updated = await updateModuleState(module.key, !module.enabled); modules.value = modules.value.map(item => item.key === updated.key ? updated : item); ElMessage.success(`${updated.label} 已${updated.enabled ? '启用' : '停用'}`) } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '模块状态更新失败') } finally { moduleUpdating.value = '' }
}
onMounted(() => { if (auth.isSuperAdmin) void loadModules() })
</script>
<template>
  <NcLayout title="系统设置" description="当前实例、会话安全和 Web UI 外观">
    <div class="settings-grid"><div class="card"><div class="card__header"><h2>实例与访问</h2></div><div class="card__body"><div class="setting-row"><div>当前用户<small>{{ auth.user?.username }} · {{ auth.user?.role }}</small></div><span class="state-pill good">已认证</span></div><div class="setting-row"><div>Web 会话<small>HttpOnly Cookie + SameSite=Lax；刷新不会影响其他浏览器</small></div><span class="state-pill">独立 session_id</span></div><div class="setting-row"><div>局域网访问<small>由服务端 NETCONSOLE_NEXT_WEB_BIND 明确开启</small></div><span class="state-pill">服务端配置</span></div><div class="setting-row"><div>高级能力<small>外部终端、文件管理、实时订阅和联动分析按 PR-2～PR-4 分阶段开放</small></div><span class="state-pill">未开放</span></div></div></div><div><div class="card"><div class="card__header"><h2>修改我的密码</h2></div><div class="card__body"><form class="form-grid" @submit.prevent="save"><label class="form-field form-field--full"><span>当前密码</span><input v-model="form.currentPassword" type="password" autocomplete="current-password" /></label><label class="form-field"><span>新密码</span><input v-model="form.newPassword" type="password" autocomplete="new-password" /></label><label class="form-field"><span>确认新密码</span><input v-model="form.confirm" type="password" autocomplete="new-password" /></label><div class="form-field form-field--full"><el-button type="primary" native-type="submit" :loading="saving">保存密码</el-button></div></form></div></div><div v-if="auth.isSuperAdmin" class="card" style="margin-top:14px"><div class="card__header"><h2>SUPERADMIN 用户管理</h2><span class="state-pill">服务端角色校验</span></div><div class="card__body"><form class="form-grid" @submit.prevent="saveManaged"><label class="form-field"><span>目标用户</span><select v-model="managed.username"><option>admin</option><option>superadmin</option></select></label><label class="form-field"><span>新密码</span><input v-model="managed.newPassword" type="password" autocomplete="new-password" /></label><label class="form-field"><span>确认新密码</span><input v-model="managed.confirm" type="password" autocomplete="new-password" /></label><div class="form-field form-field--full"><el-button type="primary" native-type="submit" :loading="saving">更新用户密码</el-button></div></form></div></div><div v-if="auth.isSuperAdmin" class="card" style="margin-top:14px"><div class="card__header"><h2>模块开放状态</h2><span class="state-pill">服务端权限校验</span></div><div class="card__body"><div v-if="modulesLoading" class="empty-state">正在读取模块状态…</div><div v-else class="settings-module-list"><div v-for="module in modules" :key="module.key" class="setting-row"><div><strong>{{ module.label }}</strong><small>{{ module.isCore ? '核心模块，始终保持启用' : `最后更新：${module.updatedBy}` }}</small></div><el-button size="small" :type="module.enabled ? 'danger' : 'success'" :loading="moduleUpdating === module.key" :disabled="module.isCore" @click="toggleModule(module)">{{ module.enabled ? '停用' : '启用' }}</el-button></div></div></div></div></div></div>
    <SiteSshJumpHostPanel />
  </NcLayout>
</template>

