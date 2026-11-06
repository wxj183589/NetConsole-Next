<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiRequestError, createConfigBackupSchedule, deleteConfigBackupSchedule, getConfigBackupSchedules, setConfigBackupScheduleEnabled, type ConfigBackupKind, type ConfigBackupSchedule } from '../api/client'

const props = defineProps<{ selectedProfileIds: string[]; configurationKind: ConfigBackupKind; contextProfileId?: string }>()
const schedules = ref<ConfigBackupSchedule[]>([])
const intervalMinutes = ref(1440)
const busy = ref(false)
const error = ref('')
let sequence = 0
const canCreate = computed(() => !busy.value && props.selectedProfileIds.length > 0 && props.selectedProfileIds.length <= 32)
function time(value?: number | null): string { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function kindLabel(kind: ConfigBackupKind): string { return { RUNNING: '运行配置', SAVED: '保存配置', STARTUP_INFO: '启动配置指向' }[kind] }
function intervalLabel(minutes: number): string { return minutes % 1440 === 0 ? `每 ${minutes / 1440} 天` : `每 ${minutes / 60} 小时` }
function stateLabel(state?: string | null): string {
  return ({ NEVER_RUN: '尚未执行', RUNNING: '执行中', SUCCEEDED: '已完成', PARTIAL: '部分完成', FAILED: '失败', BLOCKED: '无法执行', INTERRUPTED: '服务重启中断', SKIPPED: '已跳过' } as Record<string, string>)[state || ''] || (state ? '待检查' : '尚未执行')
}
function fail(cause: unknown): void {
  if (cause instanceof ApiRequestError && [401, 403, 404].includes(cause.status)) {
    schedules.value = []
    error.value = '计划不存在或当前权限不可用，请刷新设备资料。'
  } else error.value = '定时备份操作失败，请稍后刷新重试。'
}
async function load(): Promise<void> {
  const current = ++sequence
  busy.value = true; error.value = ''; schedules.value = []
  try {
    const next = await getConfigBackupSchedules(props.contextProfileId)
    if (current === sequence) schedules.value = next
  } catch (cause) { if (current === sequence) fail(cause) }
  finally { if (current === sequence) busy.value = false }
}
async function mutate(operation: () => Promise<unknown>): Promise<void> {
  if (busy.value) return
  const current = ++sequence
  busy.value = true; error.value = ''
  try {
    await operation()
    if (current !== sequence) return
    const next = await getConfigBackupSchedules(props.contextProfileId)
    if (current === sequence) schedules.value = next
  } catch (cause) { if (current === sequence) fail(cause) }
  finally { if (current === sequence) busy.value = false }
}
function create(): void {
  if (!canCreate.value) return
  const payload = { profileIds: [...props.selectedProfileIds], configurationKind: props.configurationKind, intervalMinutes: intervalMinutes.value }
  void mutate(() => createConfigBackupSchedule(payload))
}
watch(() => props.contextProfileId, () => { void load() }, { immediate: true })
onBeforeUnmount(() => { ++sequence })
</script>

<template>
  <section class="card backup-schedules" aria-label="定时配置备份">
    <div class="card__header"><h2>定时配置备份</h2><el-button size="small" :disabled="busy" @click="load">刷新计划</el-button></div>
    <div class="card__body">
      <div class="toolbar">
        <label class="form-field"><span>备份周期</span><select v-model="intervalMinutes" aria-label="备份周期" :disabled="busy"><option :value="60">每小时</option><option :value="360">每 6 小时</option><option :value="1440">每天</option><option :value="10080">每周</option><option :value="43200">每 30 天</option></select></label>
        <el-button :disabled="!canCreate" :loading="busy" @click="create">为所选设备创建计划</el-button>
        <span class="muted">{{ selectedProfileIds.length }} 台 · {{ kindLabel(configurationKind) }}</span>
      </div>
      <p class="muted">创建后经过一个周期开始执行。服务自动运行，每次执行检查当前权限；停机错过的周期不逐次补跑。</p>
      <p v-if="error" class="data-quality-note" role="status">{{ error }}</p>
      <div class="table-wrap" :aria-busy="busy">
        <table class="data-table"><thead><tr><th>设备数 / 类别</th><th>周期</th><th>计划状态</th><th>下次执行</th><th>最近执行</th><th>结果</th><th>操作</th></tr></thead><tbody>
          <tr v-for="schedule in schedules" :key="schedule.id">
            <td>{{ schedule.profileIds.length }} 台 · {{ kindLabel(schedule.configurationKind) }}</td><td>{{ intervalLabel(schedule.intervalMinutes) }}</td><td>{{ schedule.enabled ? '已启用' : '已暂停' }}</td><td>{{ schedule.enabled ? time(schedule.nextRunMs) : '—' }}</td><td>{{ time(schedule.lastRunMs) }}</td>
            <td>{{ stateLabel(schedule.lastState) }}<span v-if="schedule.lastError" class="muted"> · 请检查任务或设备权限</span><RouterLink v-if="schedule.lastBatchId" to="/tasks"> 查看任务</RouterLink></td>
            <td><el-button text size="small" :disabled="busy" @click="mutate(() => setConfigBackupScheduleEnabled(schedule.id, !schedule.enabled))">{{ schedule.enabled ? '暂停' : '启用' }}</el-button><el-button text size="small" :disabled="busy" @click="mutate(() => deleteConfigBackupSchedule(schedule.id))">删除</el-button></td>
          </tr>
          <tr v-if="!schedules.length"><td colspan="7" class="muted">{{ busy ? '正在读取计划…' : '暂无定时备份计划' }}</td></tr>
        </tbody></table>
      </div>
    </div>
  </section>
</template>
