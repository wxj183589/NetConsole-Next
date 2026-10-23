<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getHealth, type HealthSnapshot } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { useWorkspaceStore } from '../stores/workspace'

const auth = useAuthStore()
const workspace = useWorkspaceStore()
const route = useRoute()
const router = useRouter()
const health = ref<HealthSnapshot | null>(null)
const dark = ref(document.documentElement.dataset.theme === 'dark')

const nav = [
  { path: '/', label: '运行总览', icon: '◈' }, { path: '/devices', label: '设备管理', icon: '▦' },
  { path: '/terminal', label: '终端控制台', icon: '▣' }, { path: '/tasks', label: '任务中心', icon: '≡' },
  { path: '/wireless', label: '无线管理', icon: '⌁', disabled: true }, { path: '/rail', label: '轨道交通', icon: '▤', disabled: true },
  { path: '/tools', label: '工具集', icon: '⌘', disabled: true }, { path: '/files', label: '文件管理', icon: '▱', disabled: true },
  { path: '/settings', label: '系统设置', icon: '⚙' },
]
const currentLabel = computed(() => String(route.meta.label || '运行总览'))

async function navigate(item: typeof nav[number]): Promise<void> {
  if (item.disabled) { ElMessage.info(`${item.label}将在 Web PR-2 至 PR-4 中实现`) ; return }
  workspace.open(item.path, item.label)
  await router.push(item.path)
}
async function closeTab(path: string): Promise<void> {
  const next = workspace.close(path)
  if (path === route.path) await router.push(next)
}
function toggleTheme(): void {
  dark.value = !dark.value
  document.documentElement.dataset.theme = dark.value ? 'dark' : 'light'
  localStorage.setItem('netconsole-theme', dark.value ? 'dark' : 'light')
}
async function signOut(): Promise<void> { await auth.signOut(); await router.push('/login') }

onMounted(async () => {
  const saved = localStorage.getItem('netconsole-theme')
  if (saved) { dark.value = saved === 'dark'; document.documentElement.dataset.theme = saved }
  workspace.open(route.path === '/index.html' ? '/' : route.path, currentLabel.value)
  try { health.value = await getHealth() } catch { /* shell stays usable while service recovers */ }
})
watch(() => route.path, (path) => workspace.open(path, String(route.meta.label || '工作区')))
</script>

<template>
  <div class="app-shell">
    <aside class="app-sidebar">
      <div class="brand-block"><div class="brand-mark">NC</div><div class="brand-block__text"><strong>NetConsole Next</strong><small>Network Operations</small></div></div>
      <div class="nav-group"><p class="nav-group__title">工作台</p>
        <button v-for="item in nav" :key="item.path" :class="['nav-item', { active: route.path === item.path, disabled: item.disabled }]" type="button" @click="navigate(item)">
          <span class="nav-item__content"><span class="nav-item__icon">{{ item.icon }}</span>{{ item.label }}</span><span v-if="item.disabled" class="status-chip">开发中</span>
        </button>
      </div>
      <div class="sidebar-footer">本机实例<br><span class="mono">{{ health?.instanceId || 'instance-id pending' }}</span></div>
    </aside>
    <main class="app-main">
      <header class="app-header">
        <div class="breadcrumbs">工作台 / <strong>{{ currentLabel }}</strong></div>
        <div class="header-actions">
          <div class="service-indicator"><i /> 服务在线</div>
          <button class="icon-button" type="button" :title="dark ? '切换浅色主题' : '切换深色主题'" @click="toggleTheme">{{ dark ? '☼' : '☾' }}</button>
          <div class="user-menu"><span class="avatar">{{ auth.user?.username.slice(0, 1).toUpperCase() }}</span><span>{{ auth.user?.username }} · {{ auth.user?.role }}</span><button class="icon-button" title="退出登录" @click="signOut">↪</button></div>
        </div>
      </header>
      <nav class="workspace-tabs" aria-label="工作区标签">
        <button v-for="tab in workspace.tabs" :key="tab.path" :class="['workspace-tab', { active: route.path === tab.path }]" type="button" @click="router.push(tab.path)">
          <span>{{ tab.label }}</span><span v-if="!tab.pinned" class="workspace-tab__close" aria-label="关闭标签" @click.stop="closeTab(tab.path)">×</span>
        </button>
      </nav>
      <section class="page-scroll"><router-view v-slot="{ Component }"><KeepAlive><component :is="Component" /></KeepAlive></router-view></section>
    </main>
  </div>
</template>

