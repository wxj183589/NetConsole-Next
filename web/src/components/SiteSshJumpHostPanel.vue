<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { getDeviceSites, getSiteSshJumpHost, saveSiteSshJumpHost, testSiteSshJumpHost, refreshSiteSshJumpHostFingerprint, clearSiteSshJumpHostFingerprint, type DeviceSite, type SiteSshJumpHost } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const sites = ref<DeviceSite[]>([])
const siteId = ref('')
const saved = ref<SiteSshJumpHost | null>(null)
const loading = ref(false)
const busy = ref('')
const error = ref('')
const notice = ref('')
const form = reactive({ host: '', port: 22, username: '', password: '', enabled: false, clearPassword: false })
let generation = 0
const runtimeLabel = computed(() => saved.value?.runtimeStatus === 'ON_DEMAND' ? '按需待命' : saved.value?.runtimeStatus === 'CREDENTIAL_MISSING' ? '缺少密码' : saved.value ? '已停用，后续 SSH 直连' : '尚未配置')
const canProbe = computed(() => !!saved.value?.credentialConfigured && !loading.value && !busy.value)
function apply(value: SiteSshJumpHost | null): void {
  saved.value = value
  Object.assign(form, { host: value?.host || '', port: value?.port || 22, username: value?.username || '', password: '', enabled: value?.enabled || false, clearPassword: false })
}
async function load(): Promise<void> {
  const current = ++generation
  const site = siteId.value
  apply(null); error.value = ''; notice.value = ''; busy.value = ''
  loading.value = true
  try { const value = await getSiteSshJumpHost(site); if (current === generation && site === siteId.value) apply(value) }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : 'SSH 中转读取失败' }
  finally { if (current === generation) loading.value = false }
}
async function operate(action: 'save' | 'test' | 'refresh' | 'clear'): Promise<void> {
  if (!auth.isSuperAdmin || !siteId.value || loading.value || busy.value) return
  const current = generation
  const site = siteId.value
  busy.value = action; error.value = ''; notice.value = ''
  try {
    let value: SiteSshJumpHost
    if (action === 'save') {
      value = await saveSiteSshJumpHost(site, { host: form.host.trim(), port: form.port, username: form.username.trim(), enabled: form.enabled, password: form.clearPassword ? undefined : form.password || undefined, clearPassword: form.clearPassword })
    } else if (action === 'test') value = await testSiteSshJumpHost(site)
    else if (action === 'refresh') value = await refreshSiteSshJumpHostFingerprint(site)
    else value = await clearSiteSshJumpHostFingerprint(site)
    if (current !== generation || site !== siteId.value) return
    apply(value)
    notice.value = action === 'save' ? '设置已保存，自动用于后续网络设备 SSH 连接。' : action === 'clear' ? '指纹已删除，下次成功认证后自动记录。' : '中转服务器认证成功，指纹已更新。'
  } catch (cause) { if (current === generation && site === siteId.value) error.value = cause instanceof Error ? cause.message : 'SSH 中转操作失败' }
  finally { if (current === generation && site === siteId.value) busy.value = '' }
}
watch(siteId, () => { if (siteId.value) void load() })
onMounted(async () => {
  try { sites.value = await getDeviceSites(); siteId.value = sites.value[0]?.id || '' }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '局点读取失败' }
})
</script>

<template>
  <section class="card" style="margin-top: 14px" data-testid="site-ssh-jump-host">
    <div class="card__header"><h2>SSH 中转</h2><el-button size="small" :disabled="!siteId || loading || !!busy" @click="load">重新读取</el-button></div>
    <div class="card__body">
      <p class="form-hint">开启后，当前局点的网络设备 SSH、采集和 Web Shell 使用该中转；Ping、SNMP、UDP 和 Telnet 保持各自连接方式。</p>
      <label class="form-field"><span>当前局点</span><select v-model="siteId" aria-label="SSH 中转局点"><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.name }}</option></select></label>
      <p v-if="!sites.length" class="muted">暂无可访问的局点。</p>
      <p v-if="loading" role="status">正在读取当前局点配置…</p>
      <p v-if="error" class="data-quality-note" role="alert">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
      <form v-if="siteId" class="form-grid" @submit.prevent="operate('save')">
        <label class="form-field form-field--full"><span><input v-model="form.enabled" type="checkbox" :disabled="!auth.isSuperAdmin || loading || !!busy" /> 启用 SSH 中转（自动运行）</span></label>
        <label class="form-field"><span>中转服务器 IP / 主机名</span><input v-model="form.host" :disabled="!auth.isSuperAdmin || loading || !!busy" required /></label>
        <label class="form-field"><span>SSH 端口</span><input v-model.number="form.port" type="number" min="1" max="65535" :disabled="!auth.isSuperAdmin || loading || !!busy" required /></label>
        <label class="form-field"><span>用户名</span><input v-model="form.username" autocomplete="username" :disabled="!auth.isSuperAdmin || loading || !!busy" required /></label>
        <label class="form-field"><span>密码</span><input v-model="form.password" type="password" autocomplete="new-password" :disabled="!auth.isSuperAdmin || loading || !!busy || form.clearPassword" /><small>留空保留已保存密码。</small></label>
        <label v-if="auth.isSuperAdmin" class="form-field form-field--full"><span><input v-model="form.clearPassword" type="checkbox" :disabled="loading || !!busy" /> 明确清除已保存密码</span></label>
        <div v-if="auth.isSuperAdmin" class="form-field form-field--full"><div class="page-actions"><el-button type="primary" native-type="submit" :loading="busy === 'save'" :disabled="loading || !!busy">保存设置</el-button><el-button :loading="busy === 'test'" :disabled="!canProbe" @click="operate('test')">测试连接</el-button><el-button :loading="busy === 'refresh'" :disabled="!canProbe" @click="operate('refresh')">重新获取指纹</el-button><el-button :loading="busy === 'clear'" :disabled="!saved?.hostKeySha256 || loading || !!busy" @click="operate('clear')">删除指纹</el-button></div></div>
      </form>
      <template v-if="saved"><div class="setting-row"><span>运行状态</span><b>{{ runtimeLabel }}</b></div><div class="setting-row"><span>自动运行</span><b>{{ saved.enabled ? '已开启，服务重启后继续按需使用' : '未开启' }}</b></div><div class="setting-row"><span>中转服务器</span><b>{{ saved.host }}:{{ saved.port }}</b></div><div class="setting-row"><span>主机指纹</span><code style="overflow-wrap: anywhere">{{ saved.hostKeySha256 || '未记录' }}</code></div><div class="setting-row"><span>指纹管理</span><b>成功认证后自动登记和更新</b></div><div class="setting-row"><span>凭据</span><b>{{ saved.credentialConfigured ? '密码已保存' : '未保存密码' }}</b></div><p class="form-hint">连接按任务建立并释放；待命表示配置已生效，测试连接可检查当前可达性。密码保存在当前实例数据目录，请限制该目录的访问权限。</p></template>
    </div>
  </section>
</template>
