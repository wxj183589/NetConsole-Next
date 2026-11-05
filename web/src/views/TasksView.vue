<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { cancelTask, createH3cTaskDefinition, getH3cProfiles, getTaskCommands, getTasks, resumeTask, retryTask, startH3cTaskDefinition, type TaskCommand, type TaskRun, type TerminalDevice } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { canCancelTask, canResumeTask, canRetryTask, captureStatusLabel, parseStatusLabel, taskFailureLabel, taskStateLabel, taskTypeLabel } from '../taskViewModel'

const auth = useAuthStore()
const tasks = ref<TaskRun[]>([]); const loading = ref(false); const actionTaskId = ref<string | null>(null); const commandLoading = ref(false); const showCommandDialog = ref(false); const commandTask = ref<TaskRun | null>(null); const commands = ref<TaskCommand[]>([])
const profiles = ref<TerminalDevice[]>([]); const selectedProfileId = ref(''); const selectedCommands = ref<string[]>(['display version']); const planTimeoutMs = ref(30000); const planLoading = ref(false)
const planCommands = [
  ['display version', '设备版本'], ['display interface brief', '接口摘要'], ['display device', '设备状态'],
  ['display lldp neighbor-information list', 'LLDP 邻居'], ['display wlan ap all', 'FIT-AP 列表'],
] as const
async function refresh(): Promise<void> { loading.value = true; try { tasks.value = await getTasks() } catch { ElMessage.error('任务 API 读取失败') } finally { loading.value = false } }
async function refreshProfiles(): Promise<void> { try { profiles.value = await getH3cProfiles(); if (!selectedProfileId.value) selectedProfileId.value = profiles.value[0]?.id || '' } catch { profiles.value = [] } }
function time(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string { return ['COMPLETED', 'SUCCEEDED', 'RUNNING'].includes(state) ? 'good' : ['FAILED', 'INTERRUPTED'].includes(state) ? 'bad' : 'warn' }
async function showCommands(task: TaskRun): Promise<void> {
  commandTask.value = task; showCommandDialog.value = true; commands.value = []; commandLoading.value = true
  try { commands.value = await getTaskCommands(task.id) } catch { ElMessage.error('命令明细读取失败') } finally { commandLoading.value = false }
}
async function runAction(task: TaskRun, action: 'cancel' | 'retry' | 'resume'): Promise<void> {
  actionTaskId.value = task.id
  try {
    if (action === 'cancel') await cancelTask(task.id)
    else if (action === 'retry') await retryTask(task.id)
    else await resumeTask(task.id)
    ElMessage.success(action === 'cancel' ? '取消请求已提交' : action === 'retry' ? '重试任务已提交' : '恢复任务已提交')
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '任务操作失败') } finally { actionTaskId.value = null }
}
async function createAndRunPlan(): Promise<void> {
  if (!auth.isSuperAdmin || !selectedProfileId.value || !selectedCommands.value.length) { ElMessage.warning('请选择设备和至少一条只读命令'); return }
  planLoading.value = true
  try {
    const id = crypto.randomUUID().replaceAll('-', '')
    await createH3cTaskDefinition({ id, profileId: selectedProfileId.value, command: selectedCommands.value[0], commands: selectedCommands.value, timeoutMs: planTimeoutMs.value, enabled: true })
    await startH3cTaskDefinition(id)
    ElMessage.success(`已提交 ${selectedCommands.value.length} 条命令的采集计划`)
    await refresh()
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '采集计划提交失败') } finally { planLoading.value = false }
}
onMounted(async () => { await Promise.all([refresh(), refreshProfiles()]) })
</script>
<template>
  <NcLayout title="任务中心" description="采集任务的服务端状态、Raw 输出与解析状态">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <section v-if="auth.isSuperAdmin" class="card"><div class="card__header"><div><h2>新建采集计划</h2><span class="muted">固定只读命令，按顺序执行并分别保存 Raw / Parse 状态</span></div><span class="state-pill">{{ selectedCommands.length }} 条命令</span></div><div class="card__body"><div class="form-grid"><label class="form-field"><span>目标设备</span><select v-model="selectedProfileId"><option value="">选择 H3C 设备</option><option v-for="profile in profiles" :key="profile.id" :value="profile.id">{{ profile.host }}:{{ profile.port }} · {{ profile.username }}</option></select></label><label class="form-field"><span>单条命令超时</span><select v-model.number="planTimeoutMs"><option :value="10000">10 秒</option><option :value="30000">30 秒</option><option :value="60000">60 秒</option></select></label><div class="form-field form-field--full"><span>采集内容</span><div class="overview-footer"><label v-for="item in planCommands" :key="item[0]" class="check-row"><input v-model="selectedCommands" type="checkbox" :value="item[0]" />{{ item[1] }}</label></div></div></div><div class="toolbar" style="margin-top:12px"><el-button type="primary" :loading="planLoading" :disabled="!selectedProfileId || !selectedCommands.length" @click="createAndRunPlan">创建并执行计划</el-button><span class="muted">设备凭据由服务端 Credential Reference 读取，页面不接触密码。</span></div></div></section>
    <div class="card"><div class="card__header"><h2>最近任务 <span class="state-pill">{{ tasks.length }} 条</span></h2><span class="state-pill">状态由服务端实时记录</span></div><div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th>操作</th><th>状态</th><th>时间</th><th>原始数据</th><th>识别结果</th><th>说明</th><th>任务操作</th><th>明细</th></tr></thead><tbody><tr v-for="task in tasks" :key="task.id"><td><span class="device-name">{{ taskTypeLabel(task.taskType) }}</span><span class="device-subline mono" :title="`内部任务编号：${task.id}`">内部编号 {{ task.id.slice(0, 8) }}</span></td><td><span :class="['state-pill', stateClass(task.state)]">{{ taskStateLabel(task.state) }}</span></td><td><span>{{ time(task.startedMs || task.queuedMs) }}</span><span class="device-subline">{{ task.endedMs ? `完成于 ${time(task.endedMs)}` : '仍在处理' }}</span></td><td>{{ captureStatusLabel(task.captureStatus) }}</td><td>{{ parseStatusLabel(task.parseStatus) }}</td><td class="muted">{{ taskFailureLabel(task.failureReason) }}</td><td><div class="table-actions"><el-button v-if="canCancelTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'cancel')">取消</el-button><el-button v-if="canRetryTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'retry')">重试</el-button><el-button v-if="canResumeTask(task.state)" text size="small" :loading="actionTaskId === task.id" @click="runAction(task, 'resume')">恢复</el-button><span v-if="!canCancelTask(task.state) && !canRetryTask(task.state) && !canResumeTask(task.state)" class="muted">—</span></div></td><td><el-button text size="small" @click="showCommands(task)">命令明细</el-button></td></tr><tr v-if="!tasks.length"><td colspan="8"><div class="empty-state"><div><strong>暂无任务数据</strong><small>任务中心只展示服务端真实创建的任务，不使用模拟数据填充。</small></div></div></td></tr></tbody></table></div></div>
    <el-dialog v-model="showCommandDialog" :title="commandTask ? `${taskTypeLabel(commandTask.taskType)} · 命令明细` : '命令明细'" width="860px"><div v-loading="commandLoading" class="table-wrap"><table class="data-table"><thead><tr><th>#</th><th>命令</th><th>状态</th><th>Raw</th><th>解析</th><th>错误</th></tr></thead><tbody><tr v-for="command in commands" :key="command.index"><td>{{ command.index + 1 }}</td><td class="mono">{{ command.command }}</td><td><span :class="['state-pill', stateClass(command.state)]">{{ taskStateLabel(command.state) }}</span></td><td>{{ captureStatusLabel(command.captureStatus) }}</td><td>{{ parseStatusLabel(command.parseStatus) }}</td><td class="muted">{{ command.error || '—' }}</td></tr><tr v-if="!commands.length && !commandLoading"><td colspan="6" class="muted">该任务暂无命令级记录。</td></tr></tbody></table></div></el-dialog>
  </NcLayout>
</template>

