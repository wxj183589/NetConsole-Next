<script setup lang="ts">
import { computed, nextTick, onActivated, onDeactivated, onBeforeUnmount, onMounted, ref } from 'vue'
import * as echarts from 'echarts'
import { Bell, CircleCheck, DataLine, List, Monitor, TrendCharts, Warning } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import OperationalHealth from '../components/OperationalHealth.vue'
import FaultCenter from '../components/FaultCenter.vue'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
import { getDeviceInventory, getHealth, getTasks, getWirelessAcDomains, getWirelessApResources, type DeviceInventoryPage, type HealthSnapshot, type TaskRun, type WirelessPage, type WirelessAcDomain, type WirelessApResource } from '../api/client'

const health = ref<HealthSnapshot | null>(null)
const inventory = ref<DeviceInventoryPage | null>(null)
const tasks = ref<TaskRun[]>([])
const wirelessDomains = ref<WirelessPage<WirelessAcDomain> | null>(null)
const wirelessResources = ref<WirelessPage<WirelessApResource> | null>(null)
const wirelessError = ref('')
const loading = ref(true)
const statusChartHost = ref<HTMLElement | null>(null)
const faultCenterHost = ref<HTMLElement | null>(null)
function focusFaultCenter() { faultCenterHost.value?.focus({ preventScroll: true }); faultCenterHost.value?.scrollIntoView({ block: 'start' }) }
let statusChart: echarts.ECharts | undefined
let generation = 0
let active = true
const pageErrors = ref<string[]>([])

const summary = computed(() => inventory.value?.summary || {
  totalRecords: 0, validDevices: 0, invalidRecords: 0, enabledDevices: 0, disabledDevices: 0,
  onlineDevices: 0, offlineDevices: 0, unknownStatus: 0,
})
const runningTasks = computed(() => tasks.value.filter(task => ['QUEUED', 'CONNECTING', 'RUNNING'].includes(task.state)).length)
const failedTasks = computed(() => tasks.value.filter(task => ['FAILED', 'PARTIAL', 'PARTIAL_FAILED', 'FAULTED', 'TIMED_OUT', 'INTERRUPTED'].includes(task.state)).length)
const wirelessAcCount = computed(() => wirelessDomains.value?.total ?? null)
const wirelessApCount = computed(() => wirelessResources.value?.total ?? null)
const wirelessRuntimeStates = computed(() => {
  const states = wirelessDomains.value?.items.flatMap(domain => domain.members.map(member => member.runtimeState)) || []
  return { running: states.filter(state => state === 'RUNNING_MASTER' || state === 'RUNNING_BACKUP').length, unknown: states.filter(state => state === 'UNKNOWN').length }
})
const diagnosticAlerts = computed(() => {
  const items: Array<{ time: string; level: string; content: string }> = []
  if (health.value?.healthError) items.push({ time: '当前', level: health.value.healthState, content: health.value.healthError })
  if (health.value?.collectionError) items.push({ time: '当前', level: 'COLLECTION', content: health.value.collectionError })
  if ((health.value?.unresolvedIntegrityIssues || 0) > 0) items.push({ time: '当前', level: 'RAW', content: `${health.value?.unresolvedIntegrityIssues} 个 Raw 完整性问题待处理` })
  if (wirelessError.value) items.push({ time: '当前', level: 'WIRELESS', content: wirelessError.value })
  return items
})
const cpuPercent = computed(() => health.value?.resources?.processCpuPercent ?? null)
const workingSet = computed(() => formatBytes(health.value?.resources?.workingSetBytes))
const freeDisk = computed(() => formatBytes(health.value?.resources?.dataVolumeFreeBytes))

function formatBytes(value?: number | null): string {
  if (!value || value < 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let number = value; let index = 0
  while (number >= 1024 && index < units.length - 1) { number /= 1024; index++ }
  return `${number.toFixed(index ? 1 : 0)} ${units[index]}`
}
function formatTime(ms?: number | null): string { return ms ? new Date(ms).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }
function statusClass(state?: string): string {
  return state === 'HEALTHY' || state === 'COMPLETED' || state === 'CONNECTED' ? 'good' : state === 'DEGRADED' || state === 'RUNNING' || state === 'QUEUED' ? 'warn' : state === 'FAULTED' ? 'bad' : 'neutral'
}
function renderStatusChart(): void {
  if (!statusChartHost.value) return
  statusChart?.dispose()
  statusChart = echarts.init(statusChartHost.value)
  const values = [
    { name: '在线会话', value: summary.value.onlineDevices, color: '#1eb69b' },
    { name: '离线会话', value: summary.value.offlineDevices, color: '#eb5360' },
    { name: '未知状态', value: summary.value.unknownStatus, color: '#e5a018' },
  ].filter(item => item.value > 0)
  statusChart.setOption({
    animation: false,
    tooltip: { trigger: 'item' },
    legend: { bottom: 4, itemWidth: 8, itemHeight: 8, textStyle: { color: '#8292a5', fontSize: 10 } },
    series: [{ type: 'pie', radius: ['52%', '73%'], center: ['50%', '45%'], avoidLabelOverlap: true,
      label: { show: true, color: '#62758d', fontSize: 11, formatter: '{b}\n{d}%' },
      data: values.map(item => ({ ...item, itemStyle: { color: item.color } })),
    }],
  })
}
async function refresh(): Promise<void> {
  const current = ++generation
  loading.value = true
  pageErrors.value = []
  health.value = null
  inventory.value = null
  tasks.value = []
  try {
    wirelessError.value = ''
    wirelessDomains.value = null
    wirelessResources.value = null
    const result = await Promise.allSettled([
      getHealth(), getTasks(), getDeviceInventory({ page: 1, pageSize: 1 }),
      getWirelessAcDomains({ page: 1, pageSize: 100 }), getWirelessApResources({ page: 1, pageSize: 1 }),
    ])
    if (!active || current !== generation) return
    const labels = ['实例健康', '近期任务', '设备库存', 'AC Domain', 'AP 资源']
    pageErrors.value = result.flatMap((item, index) => item.status === 'rejected' ? [labels[index] + '读取失败'] : [])
    if (result[0].status === 'fulfilled') health.value = result[0].value
    if (result[1].status === 'fulfilled') tasks.value = result[1].value
    if (result[2].status === 'fulfilled') inventory.value = result[2].value
    if (result[3].status === 'fulfilled') wirelessDomains.value = result[3].value
    else wirelessError.value = '无线 AC 数据暂不可用'
    if (result[4].status === 'fulfilled') wirelessResources.value = result[4].value
    else wirelessError.value = wirelessError.value ? `${wirelessError.value}，AP 数据暂不可用` : '无线 AP 数据暂不可用'
    await nextTick(); renderStatusChart()
  } catch { ElMessage.error('无法读取运行状态，请检查服务是否仍在运行') }
  finally { if (current === generation) loading.value = false }
}
function handleResize(): void { statusChart?.resize() }
onMounted(() => { void refresh(); window.addEventListener('resize', handleResize) })
onActivated(() => { if (!active) { active = true; void refresh() } })
onDeactivated(() => { active = false; generation++ })
onBeforeUnmount(() => { active = false; generation++; window.removeEventListener('resize', handleResize); statusChart?.dispose() })
</script>

<template>
  <NcLayout title="运行总览" description="面向设备采集、终端会话与原始数据可靠性的统一工作台">
    <template #actions><el-button size="small" :loading="loading" @click="refresh">刷新数据</el-button></template>

    <p v-if="pageErrors.length" role="alert" class="bad-text">{{ pageErrors.join(' · ') }}。请刷新重试。</p>
    <div v-loading="loading" class="stats-grid">
      <article class="card stat-card"><span class="stat-card__icon"><el-icon><Monitor /></el-icon></span><div class="stat-card__label">登记设备</div><div class="stat-card__value">{{ summary.totalRecords }}</div><div class="stat-card__meta">有效 {{ summary.validDevices }} · 无效 {{ summary.invalidRecords }}</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--green"><el-icon><DataLine /></el-icon></span><div class="stat-card__label">采集任务</div><div class="stat-card__value">{{ tasks.length }}</div><div class="stat-card__meta">运行中 {{ runningTasks }} · 来源于真实 Task API</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--green"><el-icon><DataLine /></el-icon></span><div class="stat-card__label">无线 AP</div><div class="stat-card__value">{{ wirelessApCount === null ? '—' : wirelessApCount }}</div><div class="stat-card__meta">来自 AC 采集资源 API · {{ wirelessError ? '部分不可用' : '状态详情见无线工作台' }}</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--amber"><el-icon><Monitor /></el-icon></span><div class="stat-card__label">AC Domain</div><div class="stat-card__value">{{ wirelessAcCount === null ? '—' : wirelessAcCount }}</div><div class="stat-card__meta">当前页运行 {{ wirelessRuntimeStates.running }} · 未知 {{ wirelessRuntimeStates.unknown }}</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--red"><el-icon><Bell /></el-icon></span><div class="stat-card__label">服务告警</div><div class="stat-card__value">{{ diagnosticAlerts.length || '—' }}</div><div class="stat-card__meta" :class="diagnosticAlerts.length ? 'bad-text' : 'good-text'">{{ diagnosticAlerts.length ? '需要关注诊断信息' : '暂无可用告警记录' }}</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--violet"><el-icon><DataLine /></el-icon></span><div class="stat-card__label">系统资源</div><div class="stat-card__value">{{ cpuPercent === null ? '—' : `${cpuPercent.toFixed(0)}%` }}</div><div class="stat-card__meta">进程 CPU · 工作集 {{ workingSet }}</div></article>
      <article class="card stat-card"><span class="stat-card__icon stat-card__icon--amber"><el-icon><CircleCheck /></el-icon></span><div class="stat-card__label">实例健康</div><div class="stat-card__value"><span :class="['state-pill', statusClass(health?.healthState)]">{{ health?.healthState || '—' }}</span></div><div class="stat-card__meta">数据卷可用空间 {{ freeDisk }}</div></article>
    </div>

    <div class="dashboard-grid">
      <section class="card dashboard-card"><div class="card__header"><h2>设备状态分布</h2><span class="state-pill info">{{ summary.totalRecords }} 条记录</span></div><div v-if="summary.totalRecords" ref="statusChartHost" class="chart" /><div v-else class="chart-empty"><div><el-icon><Monitor /></el-icon><strong>暂无设备状态数据</strong><small>状态统计只读取服务端 Catalog 与实际终端会话，不生成示例设备。</small></div></div></section>
      <section class="card dashboard-card dashboard-alert-table"><div class="card__header"><h2>最近诊断</h2><span class="state-pill">真实 health 快照</span></div><div v-if="diagnosticAlerts.length" class="table-wrap"><table class="data-table"><thead><tr><th>时间</th><th>级别</th><th>内容</th></tr></thead><tbody><tr v-for="alert in diagnosticAlerts" :key="`${alert.level}-${alert.content}`"><td>{{ alert.time }}</td><td><span :class="['state-pill', statusClass(alert.level)]">{{ alert.level }}</span></td><td>{{ alert.content }}</td></tr></tbody></table></div><div v-else class="chart-empty"><div><el-icon><CircleCheck /></el-icon><strong>当前实例没有告警</strong><small>设备故障、无线状态与补采操作见下方设备健康工作台。</small></div></div></section>
      <section class="card dashboard-card"><div class="card__header"><h2>常用操作</h2><span class="state-pill">运维入口</span></div><nav class="card__body dashboard-shortcuts" aria-label="运维快捷操作"><RouterLink to="/devices">设备管理与连接</RouterLink><RouterLink to="/wireless">AC / AP 运行状态</RouterLink><RouterLink to="/config-backups">配置备份与比较</RouterLink><RouterLink to="/tools">Quick Ping 与路径探测</RouterLink><RouterLink to="/tasks">任务历史与证据</RouterLink><RouterLink to="/tools">Syslog 查询与导出</RouterLink><RouterLink to="/topology">拓扑与资产关系</RouterLink><a href="#dashboard-fault-center" @click.prevent="focusFaultCenter">故障历史与恢复证据</a></nav></section>
    </div>

    <div class="dashboard-grid dashboard-grid--wide">
      <section class="card"><div class="card__header"><h2>最近任务</h2><span class="state-pill info">{{ tasks.length }} 条 · 失败 {{ failedTasks }}</span></div><div v-if="tasks.length" class="table-wrap"><table class="data-table"><thead><tr><th>任务</th><th>状态</th><th>开始时间</th><th>结果</th></tr></thead><tbody><tr v-for="task in tasks.slice(0, 8)" :key="task.id"><td><span class="device-name">{{ task.taskType }}</span><span class="device-subline mono">{{ task.id }}</span></td><td><span :class="['state-pill', statusClass(task.state)]">{{ task.state }}</span></td><td>{{ formatTime(task.startedMs) }}</td><td>{{ task.failureReason || task.parseStatus || '—' }}</td></tr></tbody></table></div><div v-else class="empty-state"><div><el-icon><List /></el-icon><strong>暂无任务记录</strong><small>任务中心只展示服务端实际创建的任务，不使用模拟任务填充。</small></div></div></section>
      <section class="card"><div class="card__header"><h2>系统资源</h2><span class="state-pill">进程采样</span></div><div class="card__body resource-list"><div class="resource-row"><span>CPU</span><div class="resource-track"><span :style="{ width: `${Math.min(100, cpuPercent || 0)}%` }" /></div><b>{{ cpuPercent === null ? '—' : `${cpuPercent.toFixed(0)}%` }}</b></div><div class="resource-row"><span>内存</span><div class="resource-track"><span class="resource-track--green" :style="{ width: health?.resources?.workingSetBytes ? '100%' : '0%' }" /></div><b>{{ workingSet }}</b></div><div class="resource-row"><span>磁盘</span><div class="resource-track"><span class="resource-track--violet" :style="{ width: freeDisk === '—' ? '0%' : '100%' }" /></div><b>{{ freeDisk }}</b></div><div class="overview-footer"><span><el-icon><CircleCheck /></el-icon>线程 {{ health?.resources?.threadCount ?? '—' }}</span><span><el-icon><Warning /></el-icon>未解决 Raw {{ health?.unresolvedIntegrityIssues ?? 0 }}</span></div></div></section>
    </div>

    <OperationalHealth :can-recover="auth.user?.role === 'SUPERADMIN'" />
    <div id="dashboard-fault-center" ref="faultCenterHost" tabindex="-1"><FaultCenter :identity-key="`${auth.user?.username ?? ''}:${auth.user?.role ?? ''}`" :can-recover="auth.user?.role === 'SUPERADMIN'" /></div>

    <div class="section-label">运行边界</div>
    <div class="card card--flat"><div class="card__body overview-footer"><span><el-icon><CircleCheck /></el-icon>Raw 写入：{{ health?.rawWriteAvailable ? '可用' : '不可用' }}</span><span><el-icon><CircleCheck /></el-icon>Syslog：{{ health?.syslogEnabled ? '已启用' : '未启用' }}</span><span><el-icon><CircleCheck /></el-icon>H3C：{{ health?.h3cCollectionEnabled ? '已启用' : '未启用' }}</span><span><el-icon><Warning /></el-icon>在线状态必须由真实会话或采集结果确认</span></div></div>
  </NcLayout>
</template>

<style scoped>
.dashboard-shortcuts { display: grid; gap: 16px; }
.dashboard-shortcuts a { padding: 10px 12px; border: 1px solid var(--nc-border); border-radius: 5px; color: var(--nc-primary); }
</style>
