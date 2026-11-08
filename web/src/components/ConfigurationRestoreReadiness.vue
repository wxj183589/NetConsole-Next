<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { checkRestoreReadiness, getRestoreReadinessHistory, type RestorePreflight } from '../api/configRestoreReadiness'

const props = defineProps<{ profileId: string; backupId: string }>()
const busy = ref(false); const error = ref(''); const history = ref<RestorePreflight[]>([])
let generation = 0; let disposed = false
const labels: Record<string, string> = {
  SFTP_SUBSYSTEM_UNAVAILABLE: '设备 SFTP 子系统不可用', NATIVE_FILE_REPLACE_NOT_ADVERTISED: '未取得原生文件替换命令证据',
  DEVICE_FILE_UPLOAD_AND_HASH_UNVERIFIED: '完整文件上传、长度和设备端哈希尚未验证',
  NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED: '原生替换交互尚未验证', POST_RESTORE_AND_ROLLBACK_UNVERIFIED: '恢复后验收及故障回滚尚未验证',
  NO_COMMIT_POINT: '当前没有原生检查点', LIST_UNRECOGNIZED: '检查点列表不可识别',
  COMMIT_POINTS_PRESENT_NOT_RESTORE_VERIFIED: '已有检查点，恢复尚未验证', NOT_CHECKED: '尚未检查',
  SOURCE_INTEGRITY: '完整历史原件', CONNECT_AND_CAPTURE: '设备身份与当前配置', BUSINESS_BASELINE: '接口与 LLDP 基线',
  NATIVE_AND_TRANSFER_CAPABILITIES: '原生恢复与传输能力', INDEPENDENT_RECONNECT: '独立连接完整重读', INDEPENDENT_BUSINESS_PROBES: '独立连接业务探针',
}
function label(value: string): string { return labels[value] || value }
function result(value: string): string { return ({ RUNNING: '正在检查设备', BLOCKED: '检查已完成，恢复受阻', FAILED: '检查失败', CANCELLED: '检查已取消', INTERRUPTED: '服务重启中断', PASS: '通过', READ_ONLY_COMPLETE: '只读探测完成' } as Record<string, string>)[value] || value }
function time(value: number): string { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
async function load(): Promise<void> {
  const current = ++generation; history.value = []; error.value = ''; busy.value = false
  if (!props.profileId) return
  try { const rows = await getRestoreReadinessHistory(props.profileId); if (!disposed && current === generation) history.value = rows }
  catch { if (!disposed && current === generation) error.value = '恢复检查历史加载失败，请重试。' }
}
async function check(): Promise<void> {
  if (busy.value || !props.profileId || !props.backupId) return
  const current = ++generation; const source = { profileId: props.profileId, backupId: props.backupId }
  busy.value = true; error.value = ''
  try {
    const batch = await checkRestoreReadiness([source])
    if (!disposed && current === generation) history.value = [...batch.items, ...history.value.filter(item => !batch.items.some(next => next.id === item.id))].slice(0, 100)
  } catch { if (!disposed && current === generation) error.value = '恢复前检查未完成，请检查原件、设备连接及当前权限。' }
  finally { if (!disposed && current === generation) busy.value = false }
}
watch([() => props.profileId, () => props.backupId], () => { void load() }, { immediate: true })
onBeforeUnmount(() => { disposed = true; ++generation })
</script>

<template>
  <section aria-label="完整配置恢复前检查" class="restore-readiness">
    <h3>完整配置恢复前检查</h3>
    <p class="muted">使用选中的完整历史原件，独立连接设备两次，校验型号、版本、完整配置和接口／LLDP 基线。此操作只读；检查通过仍不代表整机恢复通过。</p>
    <div class="toolbar"><el-button :disabled="busy || !backupId" @click="check">{{ busy ? '正在检查设备…' : '检查所选原件与设备' }}</el-button><el-button :disabled="busy" @click="load">刷新检查历史</el-button></div>
    <p v-if="error" role="alert" class="data-quality-note">{{ error }}</p>
    <p v-if="!history.length && !error" class="muted">暂无恢复前检查记录。</p>
    <details v-for="item in history" :key="item.id" :open="item === history[0]">
      <summary>{{ time(item.createdMs) }} · {{ result(item.state) }} · {{ item.elapsedMs }} ms</summary>
      <p>原件 {{ item.backupId }} · 设备 {{ item.profileId }}</p>
      <div class="table-wrap"><table class="data-table"><thead><tr><th>检查阶段</th><th>结果</th><th>耗时</th></tr></thead><tbody><tr v-for="(phase, index) in item.phases" :key="index"><td>{{ label(phase.name) }}</td><td>{{ result(phase.state) }} {{ phase.error || '' }}</td><td>{{ phase.elapsedMs }} ms</td></tr></tbody></table></div>
      <p>原件完整性：{{ item.sourceIntegrityVerified ? '已验证' : '未验证' }}；设备身份：{{ item.deviceIdentityVerified ? '一致' : '未验证' }}；独立完整重读：{{ item.independentReadbackStable ? '稳定' : '未验证' }}；业务基线：{{ item.businessBaselineStable ? '稳定' : '未验证' }}。</p>
      <p>原生文件替换命令：{{ item.capabilities?.nativeFileReplaceAdvertised ? '设备已提供' : '未验证' }}；SFTP：{{ item.capabilities?.sftpAvailable ? '已连接，写权限未验证' : '不可用或未验证' }}；{{ label(item.checkpointState) }}。</p>
      <p class="muted">历史原件与当前配置：{{ item.sourceIntegrityVerified && item.deviceIdentityVerified ? (item.sourceMatchesCurrent ? '相同' : '存在差异') : '未比较' }}</p>
      <dl><dt>历史原件 SHA256</dt><dd>{{ item.sourceSha256 || '—' }}</dd><dt>首次完整重读 SHA256</dt><dd>{{ item.firstSha256 || '—' }}</dd><dt>独立完整重读 SHA256</dt><dd>{{ item.secondSha256 || '—' }}</dd><dt>业务基线 SHA256</dt><dd>{{ item.businessBaselineSha256 || '—' }}</dd></dl>
      <p>实际恢复验收：WRITE {{ item.writePass ? 'PASS' : '未执行' }} · READBACK {{ item.readbackPass ? 'PASS' : '未执行' }} · CONFIG_HASH {{ item.configHashPass ? 'PASS' : '未执行' }} · BUSINESS_PROBE {{ item.businessProbePass ? 'PASS' : '未执行' }} · ROLLBACK {{ item.rollbackPass ? 'PASS' : '未执行' }}</p>
      <ul><li v-for="blocker in item.blockers" :key="blocker">{{ label(blocker) }}</li></ul>
      <p v-if="item.error" role="alert">{{ item.error }}</p>
    </details>
  </section>
</template>

<style scoped>
.restore-readiness { border-top: 1px solid var(--border-color, #dce2eb); margin-top: 20px; padding-top: 12px; }
details { margin: 12px 0; } summary { cursor: pointer; } dd { margin-left: 0; overflow-wrap: anywhere; font-family: monospace; }
</style>
