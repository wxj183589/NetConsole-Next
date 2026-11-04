<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getTasks, type TaskRun } from '../api/client'
import { captureStatusLabel, parseStatusLabel, taskFailureLabel, taskStateLabel, taskTypeLabel } from '../taskViewModel'

const tasks = ref<TaskRun[]>([]); const loading = ref(false)
async function refresh(): Promise<void> { loading.value = true; try { tasks.value = await getTasks() } catch { ElMessage.error('任务 API 读取失败') } finally { loading.value = false } }
function time(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string { return ['COMPLETED', 'SUCCEEDED', 'RUNNING'].includes(state) ? 'good' : ['FAILED', 'INTERRUPTED'].includes(state) ? 'bad' : 'warn' }
onMounted(() => refresh())
</script>
<template>
  <NcLayout title="任务中心" description="采集任务的服务端状态、Raw 输出与解析状态">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <div class="card"><div class="card__header"><h2>最近任务 <span class="state-pill">{{ tasks.length }} 条</span></h2><span class="state-pill">状态由服务端实时记录</span></div><div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th>操作</th><th>状态</th><th>时间</th><th>原始数据</th><th>识别结果</th><th>说明</th></tr></thead><tbody><tr v-for="task in tasks" :key="task.id"><td><span class="device-name">{{ taskTypeLabel(task.taskType) }}</span><span class="device-subline mono" :title="`内部任务编号：${task.id}`">内部编号 {{ task.id.slice(0, 8) }}</span></td><td><span :class="['state-pill', stateClass(task.state)]">{{ taskStateLabel(task.state) }}</span></td><td><span>{{ time(task.startedMs || task.queuedMs) }}</span><span class="device-subline">{{ task.endedMs ? `完成于 ${time(task.endedMs)}` : '仍在处理' }}</span></td><td>{{ captureStatusLabel(task.captureStatus) }}</td><td>{{ parseStatusLabel(task.parseStatus) }}</td><td class="muted">{{ taskFailureLabel(task.failureReason) }}</td></tr><tr v-if="!tasks.length"><td colspan="6"><div class="empty-state"><div><strong>暂无任务数据</strong><small>任务中心只展示服务端真实创建的任务，不使用模拟数据填充。</small></div></div></td></tr></tbody></table></div></div>
  </NcLayout>
</template>

