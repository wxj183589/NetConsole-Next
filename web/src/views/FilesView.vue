<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { cancelArchiveJob, createArchiveJob, downloadArchive, downloadFile, getArchiveJob, getFileRoots, listFiles, openHostDirectory, type ArchiveJob, type FileEntry, type FileRoot } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const roots = ref<FileRoot[]>([])
const currentRoot = ref(sessionStorage.getItem('netconsole-files-root') || '')
const currentPath = ref(sessionStorage.getItem('netconsole-files-path') || '')
const entries = ref<FileEntry[]>([])
const search = ref(sessionStorage.getItem('netconsole-files-search') || '')
const loading = ref(false)
const page = ref(1)
const total = ref(0)
const hasMore = ref(false)
const selected = ref<string[]>([])
const job = ref<ArchiveJob | null>(null)
let poller: ReturnType<typeof setInterval> | undefined
const isSuperAdmin = computed(() => auth.user?.role === 'SUPERADMIN')
const currentRootLabel = computed(() => roots.value.find(root => root.id === currentRoot.value)?.label || currentRoot.value)

async function loadRoots(): Promise<void> {
  roots.value = await getFileRoots()
  if (!currentRoot.value || !roots.value.some(root => root.id === currentRoot.value)) currentRoot.value = roots.value[0]?.id || ''
  await loadFiles()
}
async function loadFiles(): Promise<void> {
  if (!currentRoot.value) return
  loading.value = true; selected.value = []
  try { const result = await listFiles({ rootId: currentRoot.value, path: currentPath.value, search: search.value, sort: 'name', direction: 'asc', page: page.value, pageSize: 100 }); entries.value = result.items; total.value = result.total; hasMore.value = result.hasMore; currentPath.value = result.relativePath; sessionStorage.setItem('netconsole-files-path', currentPath.value); sessionStorage.setItem('netconsole-files-search', search.value); sessionStorage.setItem('netconsole-files-root', currentRoot.value) }
  catch (cause) { entries.value = []; ElMessage.error(cause instanceof Error ? cause.message : '文件列表读取失败') }
  finally { loading.value = false }
}
function chooseRoot(root: FileRoot): void { currentRoot.value = root.id; sessionStorage.setItem('netconsole-files-root', root.id); currentPath.value = ''; page.value = 1; loadFiles() }
function openFolder(entry: FileEntry): void { if (entry.kind !== 'DIRECTORY') return; currentPath.value = entry.relativePath; page.value = 1; loadFiles() }
function up(): void { const parts = currentPath.value.split('/').filter(Boolean); parts.pop(); currentPath.value = parts.join('/'); page.value = 1; loadFiles() }
function toggle(entry: FileEntry): void { selected.value = selected.value.includes(entry.relativePath) ? selected.value.filter(path => path !== entry.relativePath) : [...selected.value, entry.relativePath] }
function formatSize(size: number): string { if (!size) return '—'; const units = ['B', 'KB', 'MB', 'GB']; let value = size; let unit = 0; while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ } return value.toFixed(unit ? 1 : 0) + ' ' + units[unit] }
function formatTime(ms: number): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function saveBlob(blob: Blob, name: string): void { const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = name; link.click(); URL.revokeObjectURL(url) }
async function downloadOne(entry: FileEntry): Promise<void> { try { saveBlob(await downloadFile(currentRoot.value, entry.relativePath), entry.name) } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '下载失败') } }
async function archiveSelection(): Promise<void> {
  if (!selected.value.length) { ElMessage.info('请选择文件或文件夹'); return }
  try { job.value = await createArchiveJob([{ rootId: currentRoot.value, paths: selected.value }]); sessionStorage.setItem('netconsole-files-archive-job', job.value.id); startPolling() } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '归档任务创建失败') }
}
async function refreshJob(): Promise<void> {
  if (!job.value) return
  try { job.value = await getArchiveJob(job.value.id); if (['COMPLETED', 'FAILED', 'CANCELLED'].includes(job.value.state)) stopPolling() } catch { sessionStorage.removeItem('netconsole-files-archive-job'); job.value = null; stopPolling() }
}
function startPolling(): void { stopPolling(); poller = setInterval(refreshJob, 1500) }
function stopPolling(): void { if (poller) clearInterval(poller); poller = undefined }
async function cancelJob(): Promise<void> { if (!job.value) return; try { await cancelArchiveJob(job.value.id); await refreshJob() } catch { ElMessage.error('取消归档失败') } }
async function downloadJob(): Promise<void> { if (!job.value) return; try { saveBlob(await downloadArchive(job.value.id), job.value.downloadName || 'netconsole-archive.zip') } catch { ElMessage.error('ZIP 下载失败') } }
async function openDirectory(): Promise<void> { try { const result = await openHostDirectory(currentRoot.value, currentPath.value); ElMessage[result.status === 'LAUNCHED' ? 'success' : 'warning'](result.detail || result.status) } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '宿主机目录打开失败') } }
async function restoreJob(): Promise<void> {
  const id = sessionStorage.getItem('netconsole-files-archive-job')
  if (!id) return
  try { job.value = await getArchiveJob(id); if (job.value.canCancel) startPolling() }
  catch { sessionStorage.removeItem('netconsole-files-archive-job') }
}
onMounted(async () => { await loadRoots(); await restoreJob() })
onBeforeUnmount(stopPolling)
</script>

<template>
  <NcLayout title="文件管理" description="服务端隔离数据目录浏览、单文件下载与后台 ZIP 归档">
    <template #actions><el-button size="small" @click="loadFiles">刷新</el-button><el-button size="small" :disabled="!isSuperAdmin" @click="openDirectory">在宿主机打开目录</el-button></template>
    <div class="file-layout">
      <aside class="card file-roots"><div class="card__header"><h2>逻辑目录</h2></div><button v-for="root in roots" :key="root.id" :class="['root-item', { active: root.id === currentRoot }]" type="button" @click="chooseRoot(root)"><span>{{ root.label }}</span><span class="status-chip">{{ root.exists ? '可用' : '空' }}</span></button><div class="card__body muted" style="font-size:11px">数据库、令牌、凭据和 reparse point 永远不属于可下载目录。</div></aside>
      <section class="card file-main">
        <div class="card__header"><div><h2>{{ currentRootLabel }}</h2><small class="mono">/{{ currentPath }}</small></div><div class="toolbar"><input v-model="search" placeholder="搜索当前目录" @keydown.enter="page = 1; loadFiles()" /><el-button size="small" :disabled="!currentPath" @click="up">上级</el-button><el-button size="small" :disabled="!selected.length" @click="archiveSelection">ZIP 归档（{{ selected.length }}）</el-button></div></div>
        <div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th style="width:42px"></th><th>名称</th><th>类型</th><th>大小</th><th>修改时间</th><th>操作</th></tr></thead><tbody><tr v-for="entry in entries" :key="entry.relativePath"><td><input type="checkbox" :checked="selected.includes(entry.relativePath)" @change="toggle(entry)" /></td><td><button v-if="entry.kind === 'DIRECTORY'" class="link-button" type="button" @click="openFolder(entry)">▱ {{ entry.name }}</button><span v-else>▤ {{ entry.name }}</span></td><td>{{ entry.kind === 'DIRECTORY' ? '文件夹' : '文件' }}</td><td>{{ formatSize(entry.size) }}</td><td>{{ formatTime(entry.modifiedMs) }}</td><td><el-button v-if="entry.canDownload" text size="small" @click="downloadOne(entry)">下载</el-button><el-button v-if="entry.kind === 'DIRECTORY'" text size="small" @click="selected = [entry.relativePath]; archiveSelection()">打包</el-button></td></tr><tr v-if="!entries.length"><td colspan="6"><div class="empty-state"><div><strong>目录为空</strong><small>不生成虚假文件；采集、终端录制或任务导出后这里才会出现真实内容。</small></div></div></td></tr></tbody></table></div>
        <div class="toolbar" style="justify-content:flex-end;margin-top:12px"><span class="muted">共 {{ total }} 项</span><el-button size="small" :disabled="page <= 1" @click="page--; loadFiles()">上一页</el-button><el-button size="small" :disabled="!hasMore" @click="page++; loadFiles()">下一页</el-button></div>
      </section>
    </div>
    <div v-if="job" class="card archive-card"><div class="card__header"><h2>后台 ZIP 任务</h2><span :class="['state-pill', job.state === 'COMPLETED' ? 'good' : job.state === 'FAILED' || job.state === 'CANCELLED' ? 'bad' : 'warn']">{{ job.state }}</span></div><div class="toolbar"><span>{{ job.processedCount }} / {{ job.selectedCount }} 文件</span><span>{{ formatSize(job.processedBytes) }} / {{ formatSize(job.totalBytes || 0) }}</span><span class="muted">{{ job.current || '等待处理' }}</span><div style="flex:1" /><el-button v-if="job.canCancel" size="small" @click="cancelJob">取消</el-button><el-button v-if="job.canDownload" size="small" type="primary" @click="downloadJob">下载 ZIP</el-button></div><small v-if="job.error" class="bad-text">{{ job.error }}</small></div>
  </NcLayout>
</template>
