<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import NcLayout from '../components/NcLayout.vue'
import { getSmartConfigurationTemplates, previewSmartConfiguration, type SmartConfigurationPreview, type SmartConfigurationTemplate } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const templates = ref<SmartConfigurationTemplate[]>([])
const selectedId = ref('')
const variables = ref<Record<string, string>>({})
const siteId = ref('')
const preview = ref<SmartConfigurationPreview | null>(null)
const loading = ref(false)
const error = ref('')
const selectedTemplate = computed(() => templates.value.find(item => item.id === selectedId.value))
const isSuperAdmin = computed(() => auth.user?.role === 'SUPERADMIN')

function selectTemplate(id: string): void {
  selectedId.value = id
  variables.value = Object.fromEntries((templates.value.find(item => item.id === id)?.variables || []).map(item => [item.name, '']))
  preview.value = null
  error.value = ''
}
async function load(): Promise<void> {
  loading.value = true
  try {
    templates.value = await getSmartConfigurationTemplates()
    if (!selectedId.value && templates.value[0]) selectTemplate(templates.value[0].id)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '模板读取失败' }
  finally { loading.value = false }
}
async function generatePreview(): Promise<void> {
  if (!selectedId.value || !isSuperAdmin.value) return
  loading.value = true; error.value = ''; preview.value = null
  try {
    preview.value = await previewSmartConfiguration({ templateId: selectedId.value, variables: variables.value, siteId: siteId.value.trim() || undefined })
    ElMessage.success('已生成配置预览，未连接设备')
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '预览生成失败' }
  finally { loading.value = false }
}
onMounted(load)
</script>

<template>
  <NcLayout title="智能配置" description="模板化生成 H3C 配置命令；当前仅支持校验和预览，不会向设备写入。">
    <template #actions><el-button size="small" :loading="loading" @click="load">刷新模板</el-button></template>
    <section class="card smart-config-card">
      <div class="card__header"><div><h2>配置模板</h2><small>选择模板并填写变量，服务端会再次校验命令边界。</small></div><span class="state-pill warn">DRY-RUN ONLY</span></div>
      <div class="card__body smart-config-form">
        <label class="form-field"><span>模板</span><select :value="selectedId" :disabled="loading" @change="selectTemplate(($event.target as HTMLSelectElement).value)"><option v-for="template in templates" :key="template.id" :value="template.id">{{ template.name }}</option></select></label>
        <label class="form-field"><span>Site（可选）</span><input v-model="siteId" placeholder="仅用于授权范围校验" /></label>
        <p v-if="selectedTemplate" class="muted smart-config-description">{{ selectedTemplate.description }}</p>
        <label v-for="variable in selectedTemplate?.variables || []" :key="variable.name" class="form-field"><span>{{ variable.label }}<small v-if="variable.required"> · 必填</small></span><input v-model="variables[variable.name]" :placeholder="variable.example" autocomplete="off" /></label>
        <div class="smart-config-actions"><el-button type="primary" :loading="loading" :disabled="!isSuperAdmin || !selectedTemplate" @click="generatePreview">生成命令预览</el-button><span v-if="!isSuperAdmin" class="state-pill warn">仅 SUPERADMIN 可生成预览</span></div>
        <p v-if="error" role="alert" class="smart-config-error">{{ error }}</p>
      </div>
    </section>
    <section v-if="preview" class="card smart-config-card">
      <div class="card__header"><div><h2>命令预览</h2><small>{{ preview.templateName }} · {{ preview.previewId }}</small></div><span class="state-pill good">{{ preview.state }}</span></div>
      <div class="card__body"><div class="smart-config-safety"><span>执行模式：{{ preview.executionMode }}</span><span>设备写入：{{ preview.deviceWriteAllowed ? '允许' : '禁止' }}</span><span>已尝试写入：{{ preview.writeAttempted ? '是' : '否' }}</span></div><ol class="smart-config-commands"><li v-for="command in preview.commands" :key="command.index"><code>{{ command.command }}</code><span class="state-pill warn">{{ command.risk }}</span></li></ol></div>
    </section>
  </NcLayout>
</template>

<style scoped>
.smart-config-card { margin-bottom: 16px; }
.smart-config-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.smart-config-description, .smart-config-actions, .smart-config-error { grid-column: 1 / -1; }
.smart-config-actions, .smart-config-safety { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.smart-config-safety { padding: 10px 12px; background: var(--nc-surface-muted); border: 1px solid var(--nc-border); border-radius: 6px; }
.smart-config-commands { grid-column: 1 / -1; margin: 0; padding: 0 0 0 28px; }
.smart-config-commands li { display: flex; align-items: center; gap: 12px; padding: 8px 0; border-bottom: 1px solid var(--nc-border); }
.smart-config-commands code { flex: 1; white-space: pre-wrap; overflow-wrap: anywhere; }
.smart-config-error { color: var(--nc-danger); }
@media (max-width: 800px) { .smart-config-form { grid-template-columns: 1fr; } }
</style>
