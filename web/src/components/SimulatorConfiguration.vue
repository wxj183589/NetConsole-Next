<script setup lang="ts">
import { ref, watch } from 'vue'
import { captureCompleteConfiguration, changeSimulatorDescription, getCompleteConfigurations, getCompleteConfigurationView, getSimulatorConfigurationChanges, type CompleteConfigurationBackup, type SimulatorConfigurationChange } from '../api/client'

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
let generation = 0
watch(() => props.profileId, () => {
  generation++; backups.value = []; changes.value = []; selected.value = ''; content.value = ''; error.value = ''; confirmed.value = false
}, { immediate: true })
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
  const [next, operations] = await Promise.all([getCompleteConfigurations(profile), getSimulatorConfigurationChanges(profile)])
  if (current !== generation) return
  backups.value = next; changes.value = operations
  if (!next.some(item => item.id === selected.value)) selected.value = next.find(item => item.restorable)?.id || ''
}
function capture() { void run(async profile => { await captureCompleteConfiguration(profile); if (props.profileId === profile) await load(profile) }) }
function view() { void run(async () => { const id = selected.value; const current = generation; const result = await getCompleteConfigurationView(id); if (current === generation && selected.value === id) content.value = result.redactedConfiguration }) }
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
      <label class="form-field"><span>写入前基线</span><select v-model="selected" aria-label="完整配置基线" :disabled="busy"><option value="">请选择已验证的完整备份</option><option v-for="item in backups" :key="item.id" :value="item.id" :disabled="!item.restorable">{{ new Date(item.capturedMs).toLocaleString() }} · {{ item.integrity }} · RESTORABLE={{ item.restorable }}</option></select></label>
      <el-button :disabled="busy || !selected" @click="view">查看脱敏完整配置</el-button>
      <pre v-if="content" style="max-height:360px;overflow:auto;white-space:pre-wrap">{{ content }}</pre>
      <div class="form-grid"><label class="form-field"><span>接口名称</span><input v-model="interfaceName" :disabled="busy" placeholder="GigabitEthernet1/0/1" /></label><label class="form-field"><span>新接口描述</span><input v-model="description" :disabled="busy || removeDescription" maxlength="80" /></label></div>
      <label class="check-row"><input v-model="removeDescription" type="checkbox" :disabled="busy" />清除该接口描述</label>
      <p class="data-quality-note">执行会修改设备接口描述，保存写入前快照并重新读取验证；失败时尝试恢复原描述，再次读取验证回滚结果。</p>
      <label class="check-row"><input v-model="confirmed" type="checkbox" :disabled="busy" />确认在当前模拟器修改指定接口描述</label>
      <el-button type="danger" :loading="busy" :disabled="busy || !confirmed || !selected || !interfaceName.trim() || (!removeDescription && !description.trim())" @click="apply">执行接口描述变更</el-button>
      <ul v-if="changes.length"><li v-for="item in changes" :key="item.id">{{ item.interfaceName }} · {{ item.state }} · {{ item.error || item.id }}</li></ul>
    </div>
  </section>
</template>
