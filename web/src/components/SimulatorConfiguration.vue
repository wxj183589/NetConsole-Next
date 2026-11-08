<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import ConfigurationRestoreReadiness from './ConfigurationRestoreReadiness.vue'
import { captureCompleteConfiguration, changeSimulatorDescription, compareCompleteConfigurations, previewCompleteConfigurationRestore, verifyNativeCheckpointRestore, getNativeCheckpointOperations, getCompleteConfigurations, getCompleteConfigurationView, getSimulatorConfigurationChanges, type CompleteConfigurationBackup, type CompleteConfigurationComparison, type CompleteConfigurationRestorePreview, type H3cCheckpointRestoreOperation, type SimulatorConfigurationChange } from '../api/client'

const props = defineProps<{ profileId: string }>()
const backups = ref<CompleteConfigurationBackup[]>([])
const changes = ref<SimulatorConfigurationChange[]>([])
const selected = ref('')
const interfaceName = ref('')
const description = ref('')
const removeDescription = ref(false)
const confirmed = ref(false)
const content = ref('')
const error = ref('')
const busy = ref(false)
const loaded = ref(false)
const beforeId = ref('')
const afterId = ref('')
const comparison = ref<CompleteConfigurationComparison | null>(null)
const restorePreview = ref<CompleteConfigurationRestorePreview | null>(null)
const category = ref('ALL')
const checkpointConfirmed = ref(false)
const checkpointOperations = ref<H3cCheckpointRestoreOperation[]>([])
const categories = ['INTERFACE', 'VLAN', 'WIRELESS', 'AP_GROUP', 'MANAGEMENT_ADDRESS', 'ACL', 'ROUTING', 'GENERAL']
const selectedBackup = computed(() => backups.value.find(item => item.id === selected.value))
const visibleContexts = computed(() => comparison.value?.contexts.filter(item => category.value === 'ALL' || item.categories.includes(category.value)) || [])
let generation = 0
watch(() => props.profileId, () => {
  generation++; backups.value = []; changes.value = []; selected.value = ''; content.value = ''; error.value = ''; confirmed.value = false
  loaded.value = false; beforeId.value = ''; afterId.value = ''; comparison.value = null; restorePreview.value = null; category.value = 'ALL'
  checkpointConfirmed.value = false; checkpointOperations.value = []
  interfaceName.value = ''; description.value = ''; removeDescription.value = false
}, { immediate: true })
watch(selected, () => { content.value = ''; confirmed.value = false; checkpointConfirmed.value = false; restorePreview.value = null })
watch([beforeId, afterId], () => { comparison.value = null; restorePreview.value = null })
async function run(operation: (profile: string) => Promise<void>) {
  if (busy.value || !props.profileId) return
  const current = generation
  busy.value = true; error.value = ''
  try { await operation(props.profileId) }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : '操作失败，请刷新后重试' }
  finally { busy.value = false }
}
async function load(profile: string) {
  const current = generation
  const [next, operations, checkpoints] = await Promise.all([getCompleteConfigurations(profile), getSimulatorConfigurationChanges(profile), getNativeCheckpointOperations(profile)])
  if (current !== generation) return
  backups.value = next; changes.value = operations; loaded.value = true
  checkpointOperations.value = checkpoints
  if (!next.some(item => item.id === selected.value && item.sourceVerified)) selected.value = next.find(item => item.sourceVerified)?.id || ''
  const valid = next.filter(item => item.sourceVerified)
  if (!valid.some(item => item.id === afterId.value)) afterId.value = valid[0]?.id || ''
  if (!valid.some(item => item.id === beforeId.value)) beforeId.value = valid[1]?.id || valid[0]?.id || ''
}
function capture() { void run(async profile => { await captureCompleteConfiguration(profile); if (props.profileId === profile) await load(profile) }) }
function view() { void run(async () => { const id = selected.value; const current = generation; const result = await getCompleteConfigurationView(id); if (current === generation && selected.value === id) content.value = result.redactedConfiguration }) }
function download(name: string, text: string, type: string) {
  const url = URL.createObjectURL(new Blob([text], { type }))
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}
function downloadBackup() { void run(async () => {
  const id = selected.value; const current = generation; const result = await getCompleteConfigurationView(id)
  if (current === generation && selected.value === id) download(`configuration-${id}-redacted.cfg`, result.redactedConfiguration, 'text/plain;charset=utf-8')
}) }
function compare() { void run(async () => {
  const before = beforeId.value; const after = afterId.value; const current = generation
  const result = await compareCompleteConfigurations(before, after)
  if (current === generation && beforeId.value === before && afterId.value === after) { comparison.value = result; restorePreview.value = null }
}) }
function previewRestore() { void run(async profile => {
  const id = selected.value; const current = generation
  const result = await previewCompleteConfigurationRestore(id)
  if (current === generation && selected.value === id) {
    await load(profile)
    if (current !== generation || selected.value !== id) return
    beforeId.value = result.currentBackupId; afterId.value = result.backupId
    // Flush selector watchers before publishing this response.
    await Promise.resolve()
    comparison.value = result.comparison; restorePreview.value = result
  }
}) }
function exportComparison() {
  if (comparison.value) download(`configuration-diff-${comparison.value.before.id}-${comparison.value.after.id}.json`, JSON.stringify(comparison.value, null, 2), 'application/json;charset=utf-8')
}
function verifyCheckpoint() {
  if (!checkpointConfirmed.value || !selected.value || !interfaceName.value.trim() || !description.value.trim() || removeDescription.value) return
  void run(async profile => {
    await verifyNativeCheckpointRestore({ profileId: profile, expectedBackupId: selected.value, interfaceName: interfaceName.value.trim(), description: description.value })
    if (props.profileId === profile) { checkpointConfirmed.value = false; await load(profile) }
  })
}
function apply() {
  if (!confirmed.value || !selected.value || !interfaceName.value.trim()) return
  void run(async profile => {
    await changeSimulatorDescription({ profileId: profile, expectedBackupId: selected.value, interfaceName: interfaceName.value.trim(), description: removeDescription.value ? null : description.value })
    if (props.profileId === profile) { confirmed.value = false; await load(profile) }
  })
}
</script>

<template>
  <section class="card" aria-label="模拟器完整配置">
    <div class="card__header"><div><h2>模拟器完整配置与接口描述</h2><small>仅授权模拟器。原始配置保存在本机，页面仅显示脱敏内容。整机恢复尚未开放。</small></div></div>
    <div class="card__body">
      <div class="toolbar"><el-button :disabled="busy" @click="run(load)">刷新完整备份</el-button><el-button :loading="busy" :disabled="busy" @click="capture">采集完整配置</el-button></div>
      <p v-if="error" role="alert">{{ error }}</p>
      <p v-if="loaded && !backups.length" role="status">暂无通过完整性校验的完整备份，请采集当前配置。</p>
      <label class="form-field"><span>写入前基线</span><select v-model="selected" aria-label="完整配置基线" :disabled="busy"><option value="">请选择已验证的完整备份</option><option v-for="item in backups" :key="item.id" :value="item.id" :disabled="!item.sourceVerified">{{ new Date(item.capturedMs).toLocaleString() }} · {{ item.integrity }} · 原件校验={{ item.sourceVerified }} · 整机可恢复={{ item.restorable }}</option></select></label>
      <el-button :disabled="busy || !selected" @click="view">查看脱敏完整配置</el-button>
      <el-button :disabled="busy || !selected" @click="downloadBackup">下载脱敏完整配置</el-button>
      <el-button :disabled="busy || !selected" @click="previewRestore">读取当前配置并预览恢复</el-button>
      <dl v-if="selectedBackup" class="backup-metadata">
        <dt>设备 / Site</dt><dd>{{ selectedBackup.deviceId }} / {{ selectedBackup.siteId }}</dd>
        <dt>型号 / 软件版本</dt><dd>{{ selectedBackup.model }} / {{ selectedBackup.softwareVersion }}</dd>
        <dt>备份版本 / 格式</dt><dd>{{ selectedBackup.version }} / {{ selectedBackup.formatVersion }}</dd>
        <dt>连接 / 来源任务</dt><dd>{{ selectedBackup.protocol }} {{ selectedBackup.host }}:{{ selectedBackup.port }} / {{ selectedBackup.taskId }}</dd>
        <dt>原始配置完整性</dt><dd>{{ selectedBackup.rawByteCount }} bytes · SHA256 {{ selectedBackup.rawSha256 }}</dd>
        <dt>脱敏副本完整性</dt><dd>{{ selectedBackup.redactedByteCount }} bytes · SHA256 {{ selectedBackup.redactedSha256 }}</dd>
      </dl>
      <pre v-if="content" style="max-height:360px;overflow:auto;white-space:pre-wrap">{{ content }}</pre>
      <h3>完整配置上下文比较</h3>
      <p>可比较任意两份当前设备的已验证历史备份。页面和导出仅包含脱敏内容，敏感值变化单独提示。</p>
      <div class="form-grid">
        <label class="form-field"><span>比较源</span><select v-model="beforeId" aria-label="比较源备份" :disabled="busy"><option value="">请选择备份</option><option v-for="item in backups" :key="item.id" :value="item.id" :disabled="!item.sourceVerified">v{{ item.version }} · {{ new Date(item.capturedMs).toLocaleString() }} · {{ item.id }}</option></select></label>
        <label class="form-field"><span>比较目标</span><select v-model="afterId" aria-label="比较目标备份" :disabled="busy"><option value="">请选择备份</option><option v-for="item in backups" :key="item.id" :value="item.id" :disabled="!item.sourceVerified">v{{ item.version }} · {{ new Date(item.capturedMs).toLocaleString() }} · {{ item.id }}</option></select></label>
      </div>
      <el-button :disabled="busy || !beforeId || !afterId" @click="compare">比较完整配置</el-button>
      <el-button :disabled="busy || !comparison" @click="exportComparison">导出脱敏比较报告</el-button>
      <p v-if="restorePreview" role="status">恢复预览：型号与版本{{ restorePreview.compatible ? '匹配' : '不匹配' }}；WRITE_ALLOWED={{ restorePreview.writeAllowed }}。阻断原因：{{ restorePreview.blockers.join(' · ') }}。本操作只采集当前完整配置，不向设备写入。</p>
      <div v-if="comparison" aria-label="配置比较结果">
        <p>原始配置{{ comparison.originalConfigurationChanged ? '存在变化' : '完全一致' }} · 脱敏行新增 {{ comparison.addedLines }} / 删除 {{ comparison.removedLines }} · 敏感值{{ comparison.sensitiveValuesChanged ? '存在变化（已隐藏）' : '未变化' }}</p>
        <p v-if="comparison.truncated" role="status">比较预览达到资源上限，部分行已省略或采用保守差异；请同时检查脱敏完整配置。</p>
        <label>配置分类 <select v-model="category" aria-label="比较配置分类"><option value="ALL">全部</option><option v-for="item in categories" :key="item" :value="item">{{ item }}</option></select></label>
        <p v-if="!visibleContexts.length">此分类没有配置变化。</p>
        <article v-for="(item, index) in visibleContexts" :key="index">
          <h4>{{ item.context }} · {{ item.categories.join(' / ') }}</h4>
          <p>新增 {{ item.addedLines }} / 删除 {{ item.removedLines }}<span v-if="item.sensitiveValuesChanged"> · 敏感值变化已隐藏</span><span v-if="item.truncated"> · 预览受限</span></p>
          <pre style="max-height:360px;overflow:auto;white-space:pre-wrap"><template v-for="(line, lineIndex) in item.lines" :key="lineIndex">{{ line.kind === 'ADDED' ? '+' : line.kind === 'REMOVED' ? '-' : ' ' }} {{ line.text }}{{ '\n' }}</template></pre>
        </article>
      </div>
      <ConfigurationRestoreReadiness :profile-id="profileId" :backup-id="selected" />
      <h3>受控接口描述变更</h3>
      <div class="form-grid"><label class="form-field"><span>接口名称</span><input v-model="interfaceName" :disabled="busy" placeholder="GigabitEthernet1/0/1" /></label><label class="form-field"><span>新接口描述</span><input v-model="description" :disabled="busy || removeDescription" maxlength="80" /></label></div>
      <label class="check-row"><input v-model="removeDescription" type="checkbox" :disabled="busy" />清除该接口描述</label>
      <p class="data-quality-note">执行会修改设备接口描述，保存写入前快照并重新读取验证；失败时尝试恢复原描述，再次读取验证回滚结果。</p>
      <label class="check-row"><input v-model="confirmed" type="checkbox" :disabled="busy" />确认在当前模拟器修改指定接口描述</label>
      <el-button type="danger" :loading="busy" :disabled="busy || !confirmed || !selected || !interfaceName.trim() || (!removeDescription && !description.trim())" @click="apply">执行接口描述变更</el-button>
      <ul v-if="changes.length"><li v-for="item in changes" :key="item.id">{{ item.interfaceName }} · {{ item.state }} · {{ item.error || item.id }}</li></ul>
      <div v-if="selectedBackup?.host === '192.168.56.3' && selectedBackup.model.startsWith('S6850')" aria-label="原生检查点恢复验证">
        <h3>SW 设备原生检查点恢复验证</h3>
        <p>创建当前完整配置的设备检查点，临时修改上方指定接口描述，再恢复刚创建的精确检查点。重新连接后验证完整配置哈希、接口链路/协议和 LLDP 邻居。设备检查点及本地前后快照保留用于追溯。</p>
        <p>此验证仅覆盖当前授权 SW 和列出的关键探针；任意本地历史配置恢复仍未开放。</p>
        <label class="check-row"><input v-model="checkpointConfirmed" aria-label="确认原生检查点恢复验证" type="checkbox" :disabled="busy" />确认创建检查点、临时变更接口描述并执行设备原生恢复</label>
        <el-button type="danger" :loading="busy" :disabled="busy || !checkpointConfirmed || !selected || !interfaceName.trim() || !description.trim() || removeDescription" @click="verifyCheckpoint">执行原生检查点恢复验证</el-button>
      </div>
      <article v-for="item in checkpointOperations" :key="item.id" aria-label="检查点恢复记录">
        <h4>{{ item.interfaceName }} · {{ item.state }} · checkpoint {{ item.checkpointId || '未创建' }}</h4>
        <p>CONFIG_WRITE_SUCCEEDED={{ item.configWriteSucceeded }} · CONFIG_READBACK_VERIFIED={{ item.configReadbackVerified }} · SERVICE_VERIFIED={{ item.serviceVerified }} · ROLLBACK_VERIFIED={{ item.rollbackVerified }}</p>
        <p>业务验证范围：{{ item.serviceVerificationScope }} · {{ item.error || item.id }}</p>
        <p>快照：before={{ item.beforeBackupId || '无' }} · changed={{ item.changedBackupId || '无' }} · restored={{ item.restoredBackupId || '无' }} · rollback={{ item.rollbackBackupId || '无' }}</p>
      </article>
    </div>
  </section>
</template>
