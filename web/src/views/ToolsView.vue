<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getHostTools, getTerminalDevices, updateHostTool, launchHostTool, type HostTool, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const tools = ref<HostTool[]>([])
const devices = ref<TerminalDevice[]>([])
const selectedDevice = ref('')
const loading = ref(false)
const saving = ref('')
const editing = ref<Record<string, string>>({})
const isSuperAdmin = () => auth.user?.role === 'SUPERADMIN'

async function refresh(): Promise<void> {
  loading.value = true
  try {
    tools.value = await getHostTools()
    tools.value.forEach(tool => { if (editing.value[tool.id] === undefined) editing.value[tool.id] = tool.executablePath || '' })
    try { devices.value = await getTerminalDevices() } catch { devices.value = [] }
    if (!selectedDevice.value) selectedDevice.value = devices.value[0]?.id || ''
  }
  catch { ElMessage.error('宿主机工具或设备列表读取失败') }
  finally { loading.value = false }
}
async function save(tool: HostTool): Promise<void> {
  saving.value = tool.id
  try { const updated = await updateHostTool(tool.id, editing.value[tool.id] || null); tools.value = tools.value.map(item => item.id === tool.id ? updated : item); ElMessage.success(tool.displayName + ' 配置已保存') }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '保存失败') }
  finally { saving.value = '' }
}
async function launch(tool: HostTool): Promise<void> {
  if (!selectedDevice.value) { ElMessage.warning('请先选择已登记设备'); return }
  try {
    const result = await launchHostTool(tool.id, selectedDevice.value)
    if (result.status === 'LAUNCHED') ElMessage.success(tool.displayName + ' 已请求宿主机桌面启动')
    else ElMessage.warning(result.detail || result.status)
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '启动请求失败') }
}
function statusLabel(value: string): string { return ({ READY: '可用', NOT_CONFIGURED: '未配置', EXECUTABLE_NOT_FOUND: '程序不存在', HOST_WINDOWS_REQUIRED: '需要 Windows 宿主机', REPARSE_POINT_BLOCKED: '路径被阻断', EXECUTABLE_NOT_ALLOWED: '不是允许的程序' } as Record<string, string>)[value] || value }
onMounted(refresh)
</script>

<template>
  <NcLayout title="工具集" description="宿主机外部终端注册与受控启动；程序运行在安装 NetConsole Next 的 Windows 宿主机桌面">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <div class="card card--flat" style="margin-bottom:14px"><div class="card__body" style="line-height:1.8;color:var(--nc-text-secondary)">外部 SecureCRT、Xshell、PuTTY 不会在访问网页的笔记本上启动。服务只通过本机 Named Pipe 请求 Desktop Helper；密码、凭据引用和任意 shell 命令都不会进入启动参数。</div></div>
    <div class="toolbar" style="margin-bottom:12px"><label class="form-field" style="min-width:300px"><span>外部终端目标设备</span><select v-model="selectedDevice"><option value="">选择已登记设备</option><option v-for="device in devices" :key="device.id" :value="device.id">{{ device.host }}:{{ device.port }} · {{ device.username }}</option></select></label><span v-if="!isSuperAdmin()" class="state-pill warn">仅 SUPERADMIN 可启动和配置</span></div>
    <div v-loading="loading" class="tool-grid">
      <article v-for="tool in tools" :key="tool.id" class="card tool-card">
        <div class="card__header"><div><h2>{{ tool.displayName }}</h2><small>{{ tool.product }} · 宿主机程序</small></div><span :class="['state-pill', tool.status === 'READY' ? 'good' : 'warn']">{{ statusLabel(tool.status) }}</span></div>
        <label class="form-field"><span>宿主机可执行文件绝对路径</span><input v-model="editing[tool.id]" :placeholder="tool.executablePath || '例如 C:\\Program Files\\...\\程序.exe'" :disabled="!isSuperAdmin()" autocomplete="off" /></label>
        <small class="muted">{{ tool.detail || '路径已通过安全检查；启动前仍由 Desktop Helper 再次校验。' }}</small>
        <div class="toolbar" style="margin-top:14px"><el-button size="small" :disabled="!isSuperAdmin() || tool.status !== 'READY' || !selectedDevice" @click="launch(tool)">启动到宿主机桌面</el-button><el-button v-if="isSuperAdmin()" size="small" type="primary" :loading="saving === tool.id" @click="save(tool)">保存路径</el-button></div>
      </article>
      <div v-if="!tools.length" class="card"><div class="empty-state"><div><strong>暂无工具注册信息</strong><small>服务没有创建虚假的安装状态。</small></div></div></div>
    </div>
  </NcLayout>
</template>
