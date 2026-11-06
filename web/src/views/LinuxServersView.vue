<script setup lang="ts">
import { computed, onActivated, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { createLinuxServer, deleteLinuxServer, getDeviceSites, getLinuxServers, testLinuxServer, updateLinuxServer, type DeviceSite, type LinuxServer, type LinuxServerWrite } from '../api/client'
import { useAuthStore } from '../stores/auth'

const router = useRouter()
const auth = useAuthStore()
const servers = ref<LinuxServer[]>([])
const sites = ref<DeviceSite[]>([])
const search = ref('')
const group = ref('')
const loading = ref(false)
const saving = ref(false)
const testingId = ref('')
const error = ref('')
const editorOpen = ref(false)
const editing = ref<LinuxServer | null>(null)
const deleting = ref<LinuxServer | null>(null)
const testResult = ref<{ name: string; status: string; message: string } | null>(null)
const form = reactive({ name: '', host: '', port: 22, username: '', password: '', clearPassword: false, groupName: '', siteId: 'local', enabled: true })
const groups = computed(() => [...new Set(servers.value.map(server => server.groupName).filter(Boolean))].sort())
const visibleServers = computed(() => {
  const query = search.value.trim().toLowerCase()
  return servers.value.filter(server => (!group.value || server.groupName === group.value) &&
    (!query || `${server.name} ${server.host} ${server.username} ${server.groupName}`.toLowerCase().includes(query)))
})
const formatTime = (value?: number | null): string => value ? new Date(value).toLocaleString('zh-CN') : '—'
const resultLabel = (value?: string | null): string => ({ CONNECTED: '连接成功', SUCCESS: '连接成功', AUTHENTICATION_FAILED: '认证失败', TIMEOUT: '连接超时', TRANSPORT_IO_ERROR: '连接中断', CREDENTIAL_UNAVAILABLE: '未保存密码', HOST_KEY_CHANGED_DURING_CONNECT: '信息已更新，请重连', UNREACHABLE: '服务器无法访问', CONNECTION_FAILED: '连接失败', FAILED: '连接失败', DISABLED: '服务器已禁用', BUSY: '连接测试繁忙' } as Record<string, string>)[value || ''] || value || '尚未连接'

async function refresh(): Promise<void> {
  loading.value = true
  error.value = ''
  try { servers.value = await getLinuxServers() }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '服务器列表读取失败' }
  finally { loading.value = false }
}
async function openEditor(server: LinuxServer | null): Promise<void> {
  try { sites.value = await getDeviceSites() }
  catch { ElMessage.error('局点读取失败'); return }
  editing.value = server
  Object.assign(form, server || { name: '', host: '', port: 22, username: '', groupName: '', siteId: sites.value[0]?.id || '', enabled: true }, { password: '', clearPassword: false })
  editorOpen.value = true
}
async function save(): Promise<void> {
  if (!form.name.trim() || !form.host.trim() || !form.username.trim() || !form.siteId) {
    ElMessage.warning('请填写名称、地址、用户名并选择局点'); return
  }
  if (!Number.isInteger(form.port) || form.port < 1 || form.port > 65535) { ElMessage.warning('端口必须介于 1 和 65535'); return }
  if (!editing.value && !form.password) { ElMessage.warning('请填写登录密码'); return }
  const payload: LinuxServerWrite = { name: form.name.trim(), host: form.host.trim(), port: form.port, username: form.username.trim(),
    password: form.clearPassword ? undefined : form.password || undefined, clearPassword: form.clearPassword,
    groupName: form.groupName.trim(), siteId: form.siteId, enabled: form.enabled }
  saving.value = true
  try {
    if (editing.value) await updateLinuxServer(editing.value.id, payload)
    else await createLinuxServer(payload)
    form.password = ''
    editorOpen.value = false
    ElMessage.success('服务器已保存，后续连接自动登录')
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '服务器保存失败') }
  finally { saving.value = false }
}
async function remove(): Promise<void> {
  if (!deleting.value) return
  saving.value = true
  try { await deleteLinuxServer(deleting.value.id); deleting.value = null; ElMessage.success('服务器已删除'); await refresh() }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '服务器删除失败') }
  finally { saving.value = false }
}
function closeDeleteDialog(value: boolean): void { if (!value) deleting.value = null }
async function test(server: LinuxServer): Promise<void> {
  testingId.value = server.id
  testResult.value = null
  try { testResult.value = { name: server.name, ...await testLinuxServer(server.id) }; await refresh() }
  catch (cause) { testResult.value = { name: server.name, status: 'FAILED', message: cause instanceof Error ? cause.message : '连接测试失败' } }
  finally { testingId.value = '' }
}
async function openTerminal(server: LinuxServer): Promise<void> {
  await router.push({ path: '/terminal', query: { linuxServerId: server.id } })
}
let mounted = false
onMounted(async () => { await refresh(); mounted = true })
onActivated(() => { if (mounted) void refresh() })
</script>

<template>
  <NcLayout title="Linux 服务器" description="管理 SSH 连接，保存密码后自动登录；终端支持实时键盘、窗口调整和多标签。">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button><el-button v-if="auth.isSuperAdmin" type="primary" @click="openEditor(null)">添加服务器</el-button></template>
    <section class="card">
      <div class="card__header"><div class="toolbar"><label class="global-search"><input v-model="search" aria-label="搜索服务器" placeholder="搜索名称、地址、用户…" /></label><select v-model="group" aria-label="服务器分组"><option value="">全部分组</option><option v-for="item in groups" :key="item" :value="item">{{ item }}</option></select><span class="muted">{{ visibleServers.length }} 台服务器</span></div></div>
      <div v-if="error" class="card__body"><p role="alert" class="data-quality-note">{{ error }}</p><el-button @click="refresh">重新加载</el-button></div>
      <div v-else-if="!servers.length" class="empty-state"><strong>{{ loading ? '正在读取服务器…' : '暂无 Linux 服务器' }}</strong><small>添加服务器后可测试连接并打开 SSH 终端。</small></div>
      <div v-else class="table-wrap"><table class="data-table" data-testid="linux-server-table"><thead><tr><th>服务器</th><th>SSH 地址</th><th>用户名</th><th>分组 / 局点</th><th>状态</th><th>最近连接</th><th>最近使用</th><th>操作</th></tr></thead><tbody>
        <tr v-for="server in visibleServers" :key="server.id" :data-server-id="server.id"><td><strong>{{ server.name }}</strong></td><td class="mono">{{ server.host }}:{{ server.port }}</td><td>{{ server.username }}</td><td>{{ server.groupName || '未分组' }}<small class="device-subline">{{ server.siteId }}</small></td><td><span :class="['state-pill', server.enabled ? 'good' : 'neutral']">{{ server.enabled ? '已启用' : '已禁用' }}</span><small class="device-subline">{{ server.credentialConfigured ? resultLabel(server.lastResult) : '未保存密码' }}</small></td><td>{{ formatTime(server.lastConnectedMs) }}</td><td>{{ formatTime(server.lastUsedMs) }}</td><td><div class="table-actions"><el-button size="small" :disabled="!server.enabled || !server.credentialConfigured" @click="openTerminal(server)">打开终端</el-button><el-button size="small" :disabled="!server.enabled || !server.credentialConfigured || !!testingId" :loading="testingId === server.id" @click="test(server)">测试连接</el-button><template v-if="auth.isSuperAdmin"><el-button size="small" @click="openEditor(server)">编辑</el-button><el-button size="small" type="danger" @click="deleting = server">删除</el-button></template></div></td></tr>
        <tr v-if="!visibleServers.length"><td colspan="8" class="muted">没有符合筛选条件的服务器</td></tr>
      </tbody></table></div>
    </section>
    <section v-if="testResult" class="card"><div class="card__body" role="status"><strong>{{ testResult.name }} · {{ resultLabel(testResult.status) }}</strong><p>{{ testResult.message }}</p></div></section>
    <el-dialog v-model="editorOpen" :title="editing ? '编辑 Linux 服务器' : '添加 Linux 服务器'" width="600px" @closed="form.password = ''"><form class="form-grid" @submit.prevent="save">
      <label class="form-field"><span>名称</span><input v-model="form.name" required /></label><label class="form-field"><span>服务器地址</span><input v-model="form.host" required /></label>
      <label class="form-field"><span>SSH 端口</span><input v-model.number="form.port" type="number" min="1" max="65535" required /></label><label class="form-field"><span>用户名</span><input v-model="form.username" autocomplete="username" required /></label>
      <label class="form-field"><span>{{ editing ? '更新密码' : '密码' }}</span><input v-model="form.password" type="password" autocomplete="new-password" :disabled="form.clearPassword" :required="!editing" /><small v-if="editing" class="form-hint">留空保留已保存的密码。</small></label><label class="form-field"><span>分组</span><input v-model="form.groupName" list="linux-server-groups" /><datalist id="linux-server-groups"><option v-for="item in groups" :key="item" :value="item" /></datalist></label>
      <label class="form-field"><span>局点</span><select v-model="form.siteId" required><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.name }}</option></select></label><label class="form-field"><span><input v-model="form.enabled" type="checkbox" /> 启用服务器</span><span v-if="editing"><input v-model="form.clearPassword" type="checkbox" /> 清除已保存密码</span></label>
      <p class="form-hint form-field--full">首次 SSH 连接自动记录指纹，指纹变化自动更新。编辑连接信息会结束该服务器已有会话。</p><div class="form-field--full"><el-button @click="editorOpen = false">取消</el-button><el-button type="primary" native-type="submit" :loading="saving">保存服务器</el-button></div>
    </form></el-dialog>
    <el-dialog :model-value="deleting !== null" title="删除 Linux 服务器" width="420px" @update:model-value="closeDeleteDialog"><p>删除 {{ deleting?.name }}（{{ deleting?.host }}）并结束其当前会话？</p><template #footer><el-button @click="deleting = null">取消</el-button><el-button type="danger" :loading="saving" @click="remove">确认删除</el-button></template></el-dialog>
  </NcLayout>
</template>
