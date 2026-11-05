<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import NcLayout from '../components/NcLayout.vue'
import { lastNavigationError } from '../navigationState'

const route = useRoute()
const router = useRouter()
const navigationFailed = computed(() => route.name === 'navigation-error' || Boolean(lastNavigationError.value))
const path = computed(() => lastNavigationError.value?.path || String(route.query.path || route.fullPath))
const message = computed(() => lastNavigationError.value?.message || '没有找到对应的模块路由')

function goHome(): void {
  lastNavigationError.value = null
  void router.push('/')
}
</script>

<template>
  <NcLayout :title="navigationFailed ? '页面加载失败' : '页面不存在'" :description="navigationFailed ? '模块加载或导航过程中发生错误' : '当前地址没有对应的已登记模块'"><div class="card"><div class="empty-state route-error-state"><div><strong>{{ navigationFailed ? '无法打开此页面' : '找不到此页面' }}</strong><small class="mono route-error-state__path">{{ path }}</small><small>{{ navigationFailed ? '请返回工作台后重试；如果问题持续，请检查本机服务状态。' : '请从左侧导航选择已登记的模块。' }}</small><button class="primary-button" type="button" @click="goHome">返回运行总览</button><details v-if="navigationFailed && message"><summary>错误详情</summary><code>{{ message }}</code></details></div></div></div></NcLayout>
</template>
