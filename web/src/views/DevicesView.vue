<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { ApiRequestError, createH3cProfile, getHostTools, getTerminalDevices, launchHostTool, type HostTool, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'

const devices = ref<TerminalDevice[]>([])
const loading = ref(false)
const unsupported = ref(false)
const showCreate = ref(false)
const form = reactive({ id: '', host: '', port: 22, username: '', credentialReference: '', hostKeySha256: '', enabled: true })
const router = useRouter()
const auth = useAuthStore()
const externalDialog = ref(false)
const externalDevice = ref<TerminalDevice | null>(null)
const externalTools = ref<HostTool[]>([])
const selectedTool = ref('')

async function refresh(): Promise<void> {
  loading.value = true; unsupported.value = false
  try { devices.value = await getTerminalDevices() }
  catch (cause) { unsupported.value = cause instanceof ApiRequestError && [404, 503].includes(cause.status); if (!unsupported.value) ElMessage.error('设备 API 读取失败') }
  finally { loading.value = false }
}
async function create(): Promise<void> {
  try { await createH3cProfile({ ...form }); ElMessage.success('设备已登记'); showCreate.value = false; Object.assign(form, { id: '', host: '', port: 22, username: '', credentialReference: '', hostKeySha256: '', enabled: true }); await refresh() }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '设备登记失败') }
}
function policyText(device: TerminalDevice): string { return `${device.terminalProtocol} · ${device.hostKeyUpdatePolicy} · ${device.terminalEncoding}` }
function openInner(device: TerminalDevice): void { router.push({ path: '/terminal', query: { deviceId: device.id } }) }
async function openExternal(device: TerminalDevice): Promise<void> {
  if (auth.user?.role !== 'SUPERADMIN') { ElMessage.warning('只有 SUPERADMIN 可以启动宿主机外部终端'); return }
  try { externalTools.value = await getHostTools(); externalDevice.value = device; selectedTool.value = externalTools.value.find(tool => tool.status === 'READY')?.id || externalTools.value[0]?.id || ''; externalDialog.value = true }
  catch { ElMessage.error('宿主机工具列表读取失败') }
}
async function launchExternal(): Promise<void> {
  if (!externalDevice.value || !selectedTool.value) return
  try { const result = await launchHostTool(selectedTool.value, externalDevice.value.id); if (result.status === 'LAUNCHED') ElMessage.success('已请求宿主机桌面启动'); else ElMessage.warning(result.detail || result.status); externalDialog.value = false }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '外部终端启动失败') }
}
onMounted(() => refresh())
</script>

<template>
  <NcLayout title="设备管理" description="查看服务端登记设备与终端兼容策略；列表来自现有 Catalog，不创建独立前端设备库">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button><el-button type="primary" size="small" @click="showCreate = true">登记设备</el-button></template>
    <div v-if="unsupported" class="card"><div class="empty-state"><div><strong>设备采集 API 尚未启用</strong><small>请在服务端开启 H3C/终端能力后重试。页面不会用模拟设备填充列表。</small></div></div></div>
    <div v-else class="card"><div class="card__header"><h2>登记设备 <span class="state-pill">{{ devices.length }} 台</span></h2><span class="state-pill">G2.4 Host Key 策略保留</span></div><div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th>设备 ID</th><th>地址</th><th>账号</th><th>状态</th><th>终端策略</th><th>最近会话</th><th>操作</th></tr></thead><tbody><tr v-for="device in devices" :key="device.id"><td class="mono">{{ device.id }}</td><td>{{ device.host }}:{{ device.port }}</td><td>{{ device.username }}</td><td><span :class="['state-pill', device.enabled ? 'good' : 'bad']">{{ device.enabled ? '已启用' : '已禁用' }}</span></td><td>{{ policyText(device) }}</td><td>{{ device.lastSessionState || '—' }}</td><td><el-button text size="small" @click="openInner(device)">内置终端</el-button><el-button v-if="auth.user?.role === 'SUPERADMIN'" text size="small" @click="openExternal(device)">外部终端</el-button></td></tr><tr v-if="!devices.length"><td colspan="7"><div class="empty-state"><div><strong>暂无登记设备</strong><small>通过“登记设备”写入真实 H3C Profile，或由 LocalManager/后端配置完成登记。</small></div></div></td></tr></tbody></table></div></div>
    <div class="section-label">安全边界</div><div class="card card--flat"><div class="card__body" style="color:var(--nc-text-secondary);font-size:12px;line-height:1.8">终端固定使用 <span class="mono">RESTRICTED_READONLY</span>；SSH Host Key、旧算法兼容、GB18030/Telnet 回退策略仍由 C# Core 校验。密码只接受 credential reference，不在 Web UI 中显示或保存明文。</div></div>
    <el-dialog v-model="showCreate" title="登记 H3C 设备" width="620px"><div class="form-grid"><label class="form-field"><span>设备 ID</span><input v-model="form.id" placeholder="例如 site-a-router-01" /></label><label class="form-field"><span>主机</span><input v-model="form.host" placeholder="192.0.2.10" /></label><label class="form-field"><span>端口</span><input v-model.number="form.port" type="number" min="1" max="65535" /></label><label class="form-field"><span>用户名</span><input v-model="form.username" /></label><label class="form-field"><span>凭据引用</span><input v-model="form.credentialReference" placeholder="Windows DPAPI reference" /></label><label class="form-field"><span>Host Key SHA-256</span><input v-model="form.hostKeySha256" placeholder="SHA256:…" /></label></div><template #footer><el-button @click="showCreate = false">取消</el-button><el-button type="primary" @click="create">保存登记</el-button></template></el-dialog>
    <el-dialog v-model="externalDialog" title="启动宿主机外部终端" width="480px"><div v-if="externalDevice" class="card card--flat" style="padding:12px;margin-bottom:12px">{{ externalDevice.host }}:{{ externalDevice.port }} · {{ externalDevice.username }}</div><label class="form-field"><span>工具</span><select v-model="selectedTool"><option v-for="tool in externalTools" :key="tool.id" :value="tool.id">{{ tool.displayName }} · {{ tool.status }}</option></select></label><template #footer><el-button @click="externalDialog = false">取消</el-button><el-button type="primary" :disabled="!selectedTool" @click="launchExternal">请求启动</el-button></template></el-dialog>
  </NcLayout>
</template>

