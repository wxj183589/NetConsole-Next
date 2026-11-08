<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { getConfigurationStorage, type ConfigurationStorage } from '../api/configurationStorage'

const props = defineProps<{ profileId: string }>()
const storage = ref<ConfigurationStorage | null>(null)
const loading = ref(false)
const error = ref('')
let generation = 0
function bytes(value: number): string { return (value / 1024 / 1024).toFixed(2) + ' MiB' }
async function load() {
  const current = ++generation
  storage.value = null; error.value = ''; loading.value = true
  try {
    const next = await getConfigurationStorage(props.profileId)
    if (current !== generation) return
    if (next.profileId !== props.profileId) throw new Error('PROFILE_MISMATCH')
    storage.value = next
  } catch { if (current === generation) error.value = '容量信息暂不可读，请检查权限或稍后重试。' }
  finally { if (current === generation) loading.value = false }
}
watch(() => props.profileId, () => { ++generation; storage.value = null; error.value = ''; loading.value = false })
onBeforeUnmount(() => { ++generation })
</script>

<template>
  <section aria-label="完整配置容量与保留策略">
    <h3>容量与保留策略</h3>
    <p class="muted">全部历史原件保留，达到容量或数量上限后停止新增采集。页面列表上限仅控制展示，不会自动删除历史。</p>
    <el-button :disabled="loading || !profileId" @click="load">{{ loading ? '正在读取容量…' : '查看配置存储容量' }}</el-button>
    <p v-if="error" role="alert">{{ error }}</p>
    <template v-if="storage">
      <p>此设备清单 {{ storage.manifestCount }} / {{ storage.maximumBackupsPerProfile }} 个，占用 {{ bytes(storage.profileStoredBytes) }}。受损清单及孤立文件也计入占用。</p>
      <p>共享配置额度 {{ bytes(storage.maximumStoredBytes) }}，当前占用 {{ storage.sharedStoredBytes === null ? '当前权限不提供全局占用' : bytes(storage.sharedStoredBytes) }}。磁盘可用 {{ bytes(storage.availableDiskBytes) }}。</p>
      <p v-if="storage.profileLimitReached || storage.sharedLimitReached" role="alert">配置存储已达到上限，请联系管理员调整容量。现有历史仍可读取。</p>
      <p class="muted">测量时间 {{ new Date(storage.measuredMs).toLocaleString('zh-CN', { hour12: false }) }}</p>
    </template>
  </section>
</template>
