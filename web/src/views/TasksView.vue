<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getTasks, type TaskRun } from '../api/client'

const tasks = ref<TaskRun[]>([]); const loading = ref(false)
async function refresh(): Promise<void> { loading.value = true; try { tasks.value = await getTasks() } catch { ElMessage.error('任务 API 读取失败') } finally { loading.value = false } }
function time(ms?: number | null): string { return ms ? new Date(ms).toLocaleString('zh-CN', { hour12: false }) : '—' }
function stateClass(state: string): string { return ['COMPLETED', 'SUCCEEDED', 'RUNNING'].includes(state) ? 'good' : ['FAILED', 'INTERRUPTED'].includes(state) ? 'bad' : 'warn' }
onMounted(() => refresh())
</script>
<template>
  <NcLayout title="任务中心" description="采集任务的服务端状态、Raw 输出与解析状态">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <div class="card"><div class="card__header"><h2>最近任务 <span class="state-pill">{{ tasks.length }} 条</span></h2><span class="state-pill">分页由 Service 负责</span></div><div v-loading="loading" class="table-wrap"><table class="data-table"><thead><tr><th>任务 ID</th><th>类型</th><th>状态</th><th>入队</th><th>完成</th><th>采集</th><th>解析</th><th>失败原因</th></tr></thead><tbody><tr v-for="task in tasks" :key="task.id"><td class="mono">{{ task.id.slice(0, 12) }}</td><td>{{ task.taskType }}</td><td><span :class="['state-pill', stateClass(task.state)]">{{ task.state }}</span></td><td>{{ time(task.queuedMs) }}</td><td>{{ time(task.endedMs) }}</td><td>{{ task.captureStatus || '—' }}</td><td>{{ task.parseStatus || '—' }}</td><td class="muted">{{ task.failureReason || '—' }}</td></tr><tr v-if="!tasks.length"><td colspan="8"><div class="empty-state"><div><strong>暂无任务数据</strong><small>页面只展示 Catalog 中真实存在的任务，不用模拟数据填充。</small></div></div></td></tr></tbody></table></div></div>
  </NcLayout>
</template>

