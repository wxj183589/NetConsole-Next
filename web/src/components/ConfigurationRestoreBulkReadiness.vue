<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { getCompleteConfigurations, type CompleteConfigurationBackup } from '../api/client'
import { checkRestoreReadiness, type RestorePreflightBatch } from '../api/configRestoreReadiness'

const props = defineProps<{ selectedProfileIds: string[] }>()
const rows = ref<Array<{ profileId: string; backups: CompleteConfigurationBackup[]; backupId: string; error: string }>>([])
const loading = ref(false); const busy = ref(false); const error = ref(''); const batch = ref<RestorePreflightBatch | null>(null)
let generation = 0; let disposed = false
const canCheck = computed(() => !loading.value && !busy.value && rows.value.length > 0 && rows.value.length <= 16 && rows.value.every(row => !!row.backupId && !row.error))
watch(() => props.selectedProfileIds.join('\0'), () => { ++generation; rows.value = []; batch.value = null; error.value = ''; loading.value = false; busy.value = false })
function date(value: number): string { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
function label(value: string): string { return ({ SFTP_SUBSYSTEM_UNAVAILABLE: '设备 SFTP 子系统不可用', NATIVE_FILE_REPLACE_NOT_ADVERTISED: '未取得原生文件替换命令证据', DEVICE_FILE_UPLOAD_AND_HASH_UNVERIFIED: '文件上传、长度和设备端哈希尚未验证', NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED: '原生替换交互尚未验证', POST_RESTORE_AND_ROLLBACK_UNVERIFIED: '恢复后验收及故障回滚尚未验证', SOURCE_INTEGRITY: '完整历史原件', CONNECT_AND_CAPTURE: '设备身份与当前配置', BUSINESS_BASELINE: '接口与 LLDP 基线', NATIVE_AND_TRANSFER_CAPABILITIES: '原生恢复与传输能力', INDEPENDENT_RECONNECT: '独立连接完整重读', INDEPENDENT_BUSINESS_PROBES: '独立连接业务探针' } as Record<string, string>)[value] || value }
function state(value: string): string { return ({ BLOCKED: '检查完成，恢复受阻', FAILED: '检查失败', PARTIAL: '部分检查完成', CANCELLED: '已取消', PASS: '通过', READ_ONLY_COMPLETE: '只读能力已探测' } as Record<string, string>)[value] || value }
async function prepare(): Promise<void> {
  if (loading.value || busy.value) return
  const profiles = [...new Set(props.selectedProfileIds)]
  const current = ++generation; rows.value = []; batch.value = null; error.value = ''
  if (!profiles.length || profiles.length > 16) { error.value = '请选择 1 至 16 台设备。'; return }
  loading.value = true
  const settled = await Promise.allSettled(profiles.map(getCompleteConfigurations))
  if (disposed || current !== generation) return
  rows.value = settled.map((result, index) => {
    const backups = result.status === 'fulfilled' ? result.value.filter(item => item.sourceVerified) : []
    return { profileId: profiles[index]!, backups, backupId: backups[0]?.id || '', error: result.status === 'rejected' ? '完整原件加载失败，请刷新。' : !backups.length ? '暂无有效完整原件，请先采集备份。' : '' }
  })
  loading.value = false
}
async function check(): Promise<void> {
  if (!canCheck.value) return
  const sources = rows.value.map(row => ({ profileId: row.profileId, backupId: row.backupId }))
  const current = ++generation; busy.value = true; error.value = ''; batch.value = null
  try { const next = await checkRestoreReadiness(sources); if (!disposed && current === generation) batch.value = next }
  catch { if (!disposed && current === generation) error.value = '批量恢复前检查未完成，请重新核对原件及设备权限。' }
  finally { if (!disposed && current === generation) busy.value = false }
}
onBeforeUnmount(() => { disposed = true; ++generation })
</script>

<template>
  <section class="restore-bulk" aria-label="多设备完整配置恢复前检查">
    <h3>多设备完整配置恢复前检查</h3>
    <p class="muted">为上方所选设备分别选择完整历史原件。逐台执行只读检查，单台失败保留其它设备的结果。当前不会上传文件、替换配置或写入设备。</p>
    <div class="toolbar"><el-button :disabled="loading || busy" @click="prepare">{{ loading ? '正在加载完整原件…' : '加载所选设备完整原件' }}</el-button><el-button :disabled="!canCheck" @click="check">{{ busy ? '正在逐台检查…' : '执行多设备恢复前检查' }}</el-button></div>
    <p v-if="error" role="alert" class="data-quality-note">{{ error }}</p>
    <div v-if="rows.length" class="table-wrap"><table class="data-table"><thead><tr><th>设备</th><th>完整历史原件</th><th>状态</th></tr></thead><tbody><tr v-for="row in rows" :key="row.profileId"><td>{{ row.backups[0]?.host || row.profileId }}</td><td><select v-model="row.backupId" @change="batch = null" :aria-label="`设备 ${row.profileId} 恢复原件`" :disabled="busy || !row.backups.length"><option value="">请选择完整原件</option><option v-for="backup in row.backups" :key="backup.id" :value="backup.id">v{{ backup.version }} · {{ date(backup.capturedMs) }} · {{ backup.model }}</option></select></td><td>{{ row.error || '原件完整性已验证，待设备检查' }}</td></tr></tbody></table></div>
    <div v-if="batch" aria-label="多设备恢复检查结果">
      <p>{{ state(batch.state) }} · {{ batch.items.length }} 台 · {{ date(batch.createdMs) }}</p>
      <article v-for="item in batch.items" :key="item.id">
        <h4>{{ rows.find(row => row.profileId === item.profileId)?.backups[0]?.host || item.profileId }} · {{ state(item.state) }} · {{ item.elapsedMs }} ms</h4>
        <p>历史原件 {{ item.backupId }} · {{ item.error || '只读检查记录已保存' }}</p>
        <p>原件完整性 {{ item.sourceIntegrityVerified ? '已验证' : '未验证' }} · 设备身份 {{ item.deviceIdentityVerified ? '一致' : '未验证' }} · 独立完整重读 {{ item.independentReadbackStable ? '稳定' : '未验证' }} · 业务基线 {{ item.businessBaselineStable ? '稳定' : '未验证' }}</p>
        <p>实际整机恢复验收：WRITE {{ item.writePass ? 'PASS' : '未执行' }} · READBACK {{ item.readbackPass ? 'PASS' : '未执行' }} · CONFIG_HASH {{ item.configHashPass ? 'PASS' : '未执行' }} · BUSINESS_PROBE {{ item.businessProbePass ? 'PASS' : '未执行' }} · ROLLBACK {{ item.rollbackPass ? 'PASS' : '未执行' }}</p>
        <p>阻断：{{ item.blockers.map(label).join(' · ') }}</p>
        <details><summary>阶段与哈希证据</summary><ul><li v-for="(phase, index) in item.phases" :key="index">{{ label(phase.name) }} · {{ state(phase.state) }} · {{ phase.elapsedMs }} ms {{ phase.error || '' }}</li></ul><p>原件 SHA256 {{ item.sourceSha256 || '—' }}</p><p>首次完整重读 {{ item.firstSha256 || '—' }}</p><p>独立完整重读 {{ item.secondSha256 || '—' }}</p><p>业务基线 {{ item.businessBaselineSha256 || '—' }}</p></details>
      </article>
    </div>
  </section>
</template>

<style scoped>
.restore-bulk { border-top: 1px solid var(--border-color, #dce2eb); margin-top: 16px; padding-top: 12px; }
article { border-bottom: 1px solid var(--border-color, #dce2eb); padding-bottom: 8px; } details p { overflow-wrap: anywhere; font-family: monospace; }
</style>
