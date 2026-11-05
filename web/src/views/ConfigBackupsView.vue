<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { createConfigBackup, downloadConfigBackup, getConfigBackupBatch, getConfigBackups, getH3cProfiles, getConfigBackupDiff, type ConfigBackupBatch, type ConfigBackupDiff, type ConfigBackupSnapshot, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const profiles = ref<TerminalDevice[]>([])
const selected = ref<string[]>([])
const profileId = ref('')
const history = ref<ConfigBackupSnapshot[]>([])
const batch = ref<ConfigBackupBatch | null>(null)
const diff = ref<ConfigBackupDiff | null>(null)
const loading = ref(false)
const comparing = ref(false)
const baseId = ref('')
const targetId = ref('')
const isSuperAdmin = computed(() => auth.user?.role === 'SUPERADMIN')
const selectedProfile = computed(() => profiles.value.find(item => item.id === profileId.value))

function time(value?: number | null): string { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function size(value: number): string { return (value / 1024).toFixed(1) + ' KB' }
function saveBlob(blob: Blob, name: string): void { const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = name; link.click(); URL.revokeObjectURL(link.href) }
async function loadHistory(): Promise<void> {
  if (!profileId.value) { history.value = []; return }
  try { history.value = await getConfigBackups(profileId.value); baseId.value = history.value[1]?.id || ''; targetId.value = history.value[0]?.id || '' } catch { history.value = []; ElMessage.error('配置备份历史读取失败') }
}
async function refresh(): Promise<void> {
  loading.value = true
  try { profiles.value = await getH3cProfiles(); if (!profileId.value && profiles.value.length) profileId.value = profiles.value[0].id; await loadHistory() }
  catch { ElMessage.error('H3C profile 读取失败') } finally { loading.value = false }
}
async function startBackup(): Promise<void> {
  if (!selected.value.length || !isSuperAdmin.value) return
  loading.value = true; batch.value = null
  try {
    batch.value = await createConfigBackup(selected.value)
    for (let index = 0; index < 1200 && batch.value && batch.value.state === 'RUNNING'; index++) {
      await new Promise(resolve => setTimeout(resolve, 250))
      batch.value = await getConfigBackupBatch(batch.value.id)
    }
    await loadHistory()
    ElMessage.success(batch.value?.state === 'SUCCEEDED' ? '配置备份完成' : '配置备份部分完成')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '配置备份失败') } finally { loading.value = false }
}
async function download(snapshot: ConfigBackupSnapshot): Promise<void> {
  try { saveBlob(await downloadConfigBackup(snapshot.id), snapshot.downloadName || ('config-v' + snapshot.version + '.cfg')) } catch { ElMessage.error('配置备份下载失败') }
}
async function compare(): Promise<void> {
  if (!baseId.value || !targetId.value || baseId.value === targetId.value) return
  comparing.value = true
  try { diff.value = await getConfigBackupDiff(baseId.value, targetId.value) } catch { diff.value = null; ElMessage.error('配置差异读取失败') } finally { comparing.value = false }
}
onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="配置备份" description="设备配置按版本保留；备份内容在服务端先做敏感字段脱敏，下载和差异均重新校验 Site 权限">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新</el-button></template>
    <section v-if="!isSuperAdmin" class="data-quality-note">当前角色不能执行配置备份；页面仍可显示已有授权历史。</section>
    <section class="card"><div class="card__header"><h2>选择设备</h2><span class="state-pill">{{ selected.length }} 台</span></div><div class="card__body"><div class="form-grid"><label v-for="profile in profiles" :key="profile.id" class="check-row"><input v-model="selected" type="checkbox" :value="profile.id" :disabled="!isSuperAdmin" />{{ profile.host }} · {{ profile.username }}</label></div><div class="toolbar" style="margin-top:12px"><el-button type="primary" :disabled="!selected.length || !isSuperAdmin" :loading="loading" @click="startBackup">开始配置备份</el-button><span v-if="batch" class="muted">批次 {{ batch.succeeded }}/{{ batch.total }} · {{ batch.state }}</span></div></div></section>
    <section v-if="selectedProfile" class="card"><div class="card__header"><div><h2>{{ selectedProfile.host }} · 版本历史</h2><small>仅显示当前 Site 可见的完整备份</small></div><select v-model="profileId" @change="loadHistory"><option v-for="profile in profiles" :key="profile.id" :value="profile.id">{{ profile.host }}</option></select></div><div class="card__body table-wrap"><table class="data-table"><thead><tr><th>版本</th><th>采集时间</th><th>大小</th><th>SHA-256</th><th>操作</th></tr></thead><tbody><tr v-for="snapshot in history" :key="snapshot.id"><td>v{{ snapshot.version }}</td><td>{{ time(snapshot.capturedMs) }}</td><td>{{ size(snapshot.byteCount) }}</td><td class="mono">{{ snapshot.sha256?.slice(0, 16) }}…</td><td><el-button text size="small" @click="download(snapshot)">下载</el-button></td></tr><tr v-if="!history.length"><td colspan="5" class="muted">暂无配置备份</td></tr></tbody></table></div></section>
    <section v-if="history.length > 1" class="card"><div class="card__header"><h2>版本差异</h2><el-button size="small" :loading="comparing" @click="compare">比较</el-button></div><div class="card__body"><div class="form-grid"><label class="form-field"><span>基线</span><select v-model="baseId"><option v-for="item in history" :key="item.id" :value="item.id">v{{ item.version }}</option></select></label><label class="form-field"><span>目标</span><select v-model="targetId"><option v-for="item in history" :key="item.id" :value="item.id">v{{ item.version }}</option></select></label></div><div v-if="diff" class="diff-grid" style="margin-top:14px"><article class="card diff-panel"><h3>新增 {{ diff.addedLines }} 行</h3><pre v-for="line in diff.added" :key="'a-' + line">+ {{ line }}</pre></article><article class="card diff-panel"><h3>删除 {{ diff.removedLines }} 行</h3><pre v-for="line in diff.removed" :key="'r-' + line">- {{ line }}</pre></article></div></div></section>
  </NcLayout>
</template>
