<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { changePassword } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore(); const saving = ref(false)
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
</script>
<template>
  <NcLayout title="系统设置" description="当前实例、会话安全和 Web UI 外观">
    <div class="settings-grid"><div class="card"><div class="card__header"><h2>实例与访问</h2></div><div class="card__body"><div class="setting-row"><div>当前用户<small>{{ auth.user?.username }} · {{ auth.user?.role }}</small></div><span class="state-pill good">已认证</span></div><div class="setting-row"><div>Web 会话<small>HttpOnly Cookie + SameSite=Lax；刷新不会影响其他浏览器</small></div><span class="state-pill">独立 session_id</span></div><div class="setting-row"><div>局域网访问<small>由服务端 NETCONSOLE_NEXT_WEB_BIND 明确开启</small></div><span class="state-pill">服务端配置</span></div><div class="setting-row"><div>高级能力<small>外部终端、文件管理、实时订阅和联动分析按 PR-2～PR-4 分阶段开放</small></div><span class="state-pill">未开放</span></div></div></div><div><div class="card"><div class="card__header"><h2>修改我的密码</h2></div><div class="card__body"><form class="form-grid" @submit.prevent="save"><label class="form-field form-field--full"><span>当前密码</span><input v-model="form.currentPassword" type="password" autocomplete="current-password" /></label><label class="form-field"><span>新密码</span><input v-model="form.newPassword" type="password" autocomplete="new-password" /></label><label class="form-field"><span>确认新密码</span><input v-model="form.confirm" type="password" autocomplete="new-password" /></label><div class="form-field form-field--full"><el-button type="primary" native-type="submit" :loading="saving">保存密码</el-button></div></form></div></div><div v-if="auth.isSuperAdmin" class="card" style="margin-top:14px"><div class="card__header"><h2>SUPERADMIN 用户管理</h2><span class="state-pill">服务端角色校验</span></div><div class="card__body"><form class="form-grid" @submit.prevent="saveManaged"><label class="form-field"><span>目标用户</span><select v-model="managed.username"><option>admin</option><option>superadmin</option></select></label><label class="form-field"><span>新密码</span><input v-model="managed.newPassword" type="password" autocomplete="new-password" /></label><label class="form-field"><span>确认新密码</span><input v-model="managed.confirm" type="password" autocomplete="new-password" /></label><div class="form-field form-field--full"><el-button type="primary" native-type="submit" :loading="saving">更新用户密码</el-button></div></form></div></div></div></div>
  </NcLayout>
</template>

