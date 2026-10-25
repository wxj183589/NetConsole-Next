<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import * as echarts from 'echarts'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getHealth, getTasks, type HealthSnapshot, type TaskRun } from '../api/client'

const health = ref<HealthSnapshot | null>(null)
const tasks = ref<TaskRun[]>([])
const loading = ref(true)
const chartHost = ref<HTMLElement | null>(null)
let chart: echarts.ECharts | undefined

function formatBytes(value = 0): string { return value > 1024 * 1024 ? `${(value / 1024 / 1024).toFixed(1)} MB` : `${Math.round(value / 1024)} KB` }
function statusClass(value?: string): string { return value === 'HEALTHY' || value === 'RUNNING' || value === 'COMPLETED' ? 'good' : value === 'DEGRADED' || value === 'QUEUED' ? 'warn' : 'bad' }
function renderChart(): void {
  if (!chartHost.value) return
  chart?.dispose(); chart = echarts.init(chartHost.value)
  const now = Date.now(); const received = health.value?.udpReceived || 0
  chart.setOption({
    animation: false, grid: { left: 42, right: 16, top: 22, bottom: 28 },
    tooltip: { trigger: 'axis' }, xAxis: { type: 'category', boundaryGap: false, data: ['-5m', '-4m', '-3m', '-2m', '-1m', 'now'], axisLabel: { color: '#8a99aa' } },
    yAxis: { type: 'value', min: 0, splitLine: { lineStyle: { color: 'rgba(120,140,160,.16)' } }, axisLabel: { color: '#8a99aa' } },
    series: [{ type: 'line', smooth: true, showSymbol: false, data: [0, 0, 0, 0, 0, received], lineStyle: { color: '#2b7fff', width: 2 }, areaStyle: { color: 'rgba(43,127,255,.13)' } }],
    title: received ? undefined : { text: '暂无实时采集曲线', left: 'center', top: '44%', textStyle: { color: '#8a99aa', fontSize: 13, fontWeight: 400 } },
  })
  void now
}
async function refresh(): Promise<void> {
  loading.value = true
  try { [health.value, tasks.value] = await Promise.all([getHealth(), getTasks()]); await nextTick(); renderChart() }
  catch { ElMessage.error('无法读取服务状态，请检查服务是否仍在运行') }
  finally { loading.value = false }
}
onMounted(() => { void refresh(); window.addEventListener('resize', () => chart?.resize()) })
onBeforeUnmount(() => chart?.dispose())
</script>

<template>
  <NcLayout title="运行总览" description="采集服务、原始数据与任务执行状态的统一入口">
    <template #actions><el-button size="small" @click="refresh">刷新</el-button></template>
    <div v-loading="loading" class="stats-grid">
      <div class="card stat-card"><div class="stat-card__label">服务状态</div><div class="stat-card__value"><span :class="['state-pill', statusClass(health?.healthState)]">{{ health?.healthState || '读取中' }}</span></div><div class="stat-card__meta">{{ health?.collectionState || '等待服务响应' }}</div></div>
      <div class="card stat-card"><div class="stat-card__label">UDP 接收记录</div><div class="stat-card__value">{{ health?.udpReceived ?? '—' }}</div><div class="stat-card__meta good">Raw 已落盘 {{ health?.rawFlushedRecords ?? 0 }} 条</div></div>
      <div class="card stat-card"><div class="stat-card__label">原始数据段</div><div class="stat-card__value">{{ health?.rawSegments?.finalized ?? '—' }}</div><div class="stat-card__meta">未完成 {{ health?.rawSegments?.incomplete ?? 0 }} · 缺失 {{ health?.rawSegments?.missing ?? 0 }}</div></div>
      <div class="card stat-card"><div class="stat-card__label">活动 SSH 会话</div><div class="stat-card__value">{{ health?.h3cMetrics?.activeSshSessions ?? 0 }}</div><div class="stat-card__meta">内存 {{ formatBytes(health?.resources?.workingSetBytes) }}</div></div>
    </div>
    <div class="dashboard-grid">
      <div class="card"><div class="card__header"><h2>采集吞吐</h2><span class="state-pill">快照视图 · SignalR 待 PR-3</span></div><div ref="chartHost" class="chart" /></div>
      <div class="card"><div class="card__header"><h2>最近任务</h2><span class="state-pill">真实 API</span></div><div v-if="tasks.length" class="table-wrap"><table class="data-table"><thead><tr><th>任务</th><th>状态</th></tr></thead><tbody><tr v-for="task in tasks.slice(0, 6)" :key="task.id"><td class="mono">{{ task.taskType }}</td><td><span :class="['state-pill', statusClass(task.state)]">{{ task.state }}</span></td></tr></tbody></table></div><div v-else class="empty-state"><div><strong>暂无任务记录</strong><small>任务中心将展示服务端实际创建的采集任务，不使用模拟设备或模拟任务数据。</small></div></div></div>
    </div>
    <div class="section-label">运行约束</div>
    <div class="card card--flat"><div class="card__body" style="display:flex; gap:22px; flex-wrap:wrap; color:var(--nc-text-secondary); font-size:12px"><span>✓ Raw 写入：{{ health?.rawWriteAvailable ? '可用' : '不可用' }}</span><span>✓ Syslog：{{ health?.syslogEnabled ? '已启用' : '未启用' }}</span><span>✓ H3C 采集：{{ health?.h3cCollectionEnabled ? '已启用' : '未启用' }}</span><span>! 未解决完整性问题：{{ health?.unresolvedIntegrityIssues ?? 0 }}</span></div></div>
  </NcLayout>
</template>

