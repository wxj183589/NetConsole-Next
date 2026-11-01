<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { createSite, getSites, updateSite, updateSiteStatus, type Site, type SiteStatus } from '../api/client'
import { useSiteContextStore } from '../stores/siteContext'

const context = useSiteContextStore()
const sites = ref<Site[]>([])
const loading = ref(false)
const saving = ref(false)
const editorOpen = ref(false)
const editing = ref<Site | null>(null)
const form = reactive({ id: '', name: '', description: '' })

function formatTime(value: number): string { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function statusLabel(status: SiteStatus): string { return status === 'ACTIVE' ? '启用' : status === 'DISABLED' ? '停用' : '已归档' }
function statusClass(status: SiteStatus): string { return status === 'ACTIVE' ? 'good' : status === 'DISABLED' ? 'warn' : 'neutral' }

async function refresh(): Promise<void> {
  loading.value = true
  try { sites.value = await getSites(); await context.refresh() }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '局点列表读取失败') }
  finally { loading.value = false }
}
function openCreate(): void { editing.value = null; Object.assign(form, { id: '', name: '', description: '' }); editorOpen.value = true }
function openEdit(site: Site): void { editing.value = site; Object.assign(form, { id: site.id, name: site.name, description: site.description }); editorOpen.value = true }
async function save(): Promise<void> {
  if (!form.id.trim() || !form.name.trim()) { ElMessage.warning('局点标识和名称不能为空'); return }
  saving.value = true
  try {
    if (editing.value) await updateSite(editing.value.id, { ...form, expectedUpdatedMs: editing.value.updatedMs })
    else await createSite(form)
    editorOpen.value = false
    ElMessage.success(editing.value ? '局点已更新' : '局点已创建')
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '局点保存失败') }
  finally { saving.value = false }
}
async function changeStatus(site: Site, status: SiteStatus): Promise<void> {
  const action = status === 'ARCHIVED' ? '归档' : status === 'DISABLED' ? '停用' : '重新启用'
  const refs = site.deviceCount + site.groupCount + site.collectionSourceCount + site.terminalProfileCount
  try {
    await ElMessageBox.confirm(refs ? `该局点仍关联 ${refs} 项数据。${action}不会删除数据，后续写入需要重新启用。是否继续？` : `确认${action}局点“${site.name}”？`, `确认${action}`, { type: status === 'ACTIVE' ? 'info' : 'warning' })
    await updateSiteStatus(site.id, status, site.updatedMs)
    ElMessage.success(`局点已${action}`)
    await refresh()
  } catch (cause) {
    if (cause !== 'cancel' && cause !== 'close') ElMessage.error(cause instanceof Error ? cause.message : `${action}失败`)
  }
}
onMounted(() => { void refresh() })
</script>

<template>
  <NcLayout title="局点管理" description="局点是全局数据、任务和权限范围；设备分组与轨道交通组织分别归属于局点。">
    <template #actions><el-button :loading="loading" @click="refresh">刷新</el-button><el-button type="primary" @click="openCreate">新增局点</el-button></template>
    <div class="site-management-note card"><div><strong>稳定标识与安全边界</strong><p>局点标识创建后不可修改；停用或归档不会硬删除设备、分组、采集源或历史记录。当前用户只能看到服务端授权范围内的局点。</p></div><span class="state-pill">SUPERADMIN 管理</span></div>
    <section class="card"><div v-loading="loading" class="table-wrap"><table class="data-table site-table"><thead><tr><th>局点</th><th>状态</th><th>设备</th><th>分组</th><th>采集源 / 终端</th><th>更新时间</th><th>操作</th></tr></thead><tbody><tr v-for="site in sites" :key="site.id"><td><strong>{{ site.name }}</strong><span class="device-subline mono">{{ site.id }}</span><span v-if="site.description" class="device-subline">{{ site.description }}</span></td><td><span :class="['state-pill', statusClass(site.status)]">{{ statusLabel(site.status) }}</span></td><td>{{ site.deviceCount }}</td><td>{{ site.groupCount }}</td><td>{{ site.collectionSourceCount }} / {{ site.terminalProfileCount }}</td><td>{{ formatTime(site.updatedMs) }}</td><td><div class="table-actions"><el-button text size="small" @click="openEdit(site)">编辑</el-button><el-button v-if="site.status === 'ACTIVE' && site.id !== 'local'" text size="small" @click="changeStatus(site, 'DISABLED')">停用</el-button><el-button v-if="site.status === 'DISABLED'" text size="small" @click="changeStatus(site, 'ACTIVE')">启用</el-button><el-button v-if="site.status !== 'ARCHIVED' && site.id !== 'local'" text size="small" @click="changeStatus(site, 'ARCHIVED')">归档</el-button></div></td></tr><tr v-if="!sites.length"><td colspan="7"><div class="empty-state"><div><strong>暂无可管理局点</strong><small>局点列表为空或当前身份没有可信 Site Scope 授权。不会显示模拟数据。</small></div></div></td></tr></tbody></table></div></section>
    <el-dialog v-model="editorOpen" :title="editing ? '编辑局点' : '新增局点'" width="520px"><form class="form-grid" @submit.prevent="save"><label class="form-field"><span>局点标识</span><input v-model="form.id" :disabled="!!editing" placeholder="例如 hzl10" /></label><label class="form-field"><span>局点名称</span><input v-model="form.name" placeholder="例如 杭州地铁 10 号线" /></label><label class="form-field form-field--full"><span>基本信息</span><textarea v-model="form.description" rows="4" placeholder="可选：项目、线路或运维范围说明" /></label></form><template #footer><el-button @click="editorOpen = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存</el-button></template></el-dialog>
  </NcLayout>
</template>
