<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { DataLine, Guide, Operation, Search, UserFilled } from '@element-plus/icons-vue'
import { getDeviceInventory, getHealth, getModuleStates, type HealthSnapshot, type ModuleState } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { useWorkspaceStore } from '../stores/workspace'
import { moduleRegistry } from '../moduleRegistry'

const auth = useAuthStore()
const workspace = useWorkspaceStore()
const route = useRoute()
const router = useRouter()
const health = ref<HealthSnapshot | null>(null)
const deviceCount = ref<number | null>(null)
const dark = ref(document.documentElement.dataset.theme === 'dark')
const globalSearch = ref('')
const sidebarCollapsed = ref(false)
const moduleStates = ref<ModuleState[]>([])

const nav = computed(() => moduleRegistry.map(item => {
  const persisted = moduleStates.value.find(state => state.key === item.key)
  if (!persisted) return item
  return {
    ...item,
    status: persisted.enabled && item.status === 'READY' ? 'READY' as const : 'DISABLED' as const,
    disabledReason: persisted.enabled ? item.disabledReason : '模块已由管理员停用',
  }
}))
const currentLabel = computed(() => String(route.meta.label || '运行总览'))
const isTerminal = computed(() => route.path === '/terminal')
const healthLabel = computed(() => ({ HEALTHY: '服务正常', DEGRADED: '服务降级', FAULTED: '服务故障' } as Record<string, string>)[health.value?.healthState || ''] || '服务状态未知')
const healthTone = computed(() => ({ HEALTHY: 'success', DEGRADED: 'warning', FAULTED: 'danger' } as Record<string, string>)[health.value?.healthState || ''] || 'unknown')
const queueLabel = computed(() => health.value?.collectionState === 'running' ? '队列状态正常' : health.value?.collectionState || '队列状态未知')

async function navigate(item: (typeof moduleRegistry)[number]): Promise<void> {
  if (item.status === 'DISABLED') { ElMessage.info(item.disabledReason || `${item.label}尚未接入真实业务 API`); return }
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
async function submitGlobalSearch(): Promise<void> {
  if (!globalSearch.value.trim()) return
  workspace.open('/devices', '设备管理')
  await router.push({ path: '/devices', query: { q: globalSearch.value.trim() } })
}
async function refreshShell(): Promise<void> {
  const [healthResult, modulesResult, inventoryResult] = await Promise.allSettled([
    getHealth(), getModuleStates(), getDeviceInventory({ page: 1, pageSize: 1 }),
  ])
  health.value = healthResult.status === 'fulfilled' ? healthResult.value : null
  if (modulesResult.status === 'fulfilled') moduleStates.value = modulesResult.value
  deviceCount.value = inventoryResult.status === 'fulfilled' ? inventoryResult.value.summary.onlineDevices : null
}

onMounted(async () => {
  const saved = localStorage.getItem('netconsole-theme')
  if (saved) { dark.value = saved === 'dark'; document.documentElement.dataset.theme = saved }
  workspace.open(route.path === '/index.html' ? '/' : route.path, currentLabel.value)
  await refreshShell()
})
watch(() => route.path, (path) => { workspace.open(path, String(route.meta.label || '工作区')); void refreshShell() })
</script>

<template>
  <div :class="['app-shell', { 'app-shell--collapsed': sidebarCollapsed, 'app-shell--terminal': isTerminal }]">
    <aside class="app-sidebar">
      <div class="brand-block">
        <div class="brand-mark"><el-icon><Connection /></el-icon></div>
        <div class="brand-block__text"><strong>NetConsole <em>Next</em></strong><small>统一终端接入 · 设备采集 · 运维审计</small></div>
      </div>
      <div class="nav-group">
        <p class="nav-group__title">工作台</p>
        <button v-for="item in nav" :key="item.path" :class="['nav-item', { active: route.path === item.path, disabled: item.status === 'DISABLED' }]" type="button" :title="item.label" @click="navigate(item)">
          <span class="nav-item__content"><el-icon class="nav-item__icon"><component :is="item.icon" /></el-icon><span class="nav-item__label">{{ item.label }}</span></span>
          <span v-if="item.status === 'DISABLED'" class="status-chip">未开放</span>
        </button>
      </div>
      <div class="sidebar-footer"><div class="sidebar-footer__line"><span :class="['sidebar-dot', `status-dot--${healthTone}`]" />{{ healthLabel }}</div><span class="mono sidebar-instance">{{ health?.instanceId || 'instance-id pending' }}</span></div>
      <button class="sidebar-collapse" type="button" :title="sidebarCollapsed ? '展开导航' : '收起导航'" @click="sidebarCollapsed = !sidebarCollapsed"><el-icon><Guide /></el-icon></button>
    </aside>

    <main class="app-main">
      <header class="app-header">
        <div class="header-context"><span class="header-context__product">NetConsole Next</span><span class="header-context__divider">/</span><strong>{{ currentLabel }}</strong></div>
        <div class="header-actions">
          <label class="global-search"><el-icon><Search /></el-icon><input v-model="globalSearch" placeholder="搜索设备、IP、任务…" @keydown.enter="submitGlobalSearch" /><kbd>⌘ K</kbd></label>
          <div class="header-status"><span class="header-status__item"><i :class="['status-dot', `status-dot--${healthTone}`]" />{{ healthLabel }}</span><span class="header-status__item header-status__queue"><el-icon><DataLine /></el-icon>{{ queueLabel }}</span><span class="header-status__item">在线设备 <b>{{ deviceCount ?? '—' }}</b></span></div>
          <button class="header-icon-button" type="button" :title="dark ? '切换浅色主题' : '切换深色主题'" @click="toggleTheme"><el-icon><Operation /></el-icon></button>
          <div class="user-menu"><span class="avatar"><el-icon><UserFilled /></el-icon></span><span class="user-menu__name">{{ auth.user?.username }}</span><span class="user-menu__role">{{ auth.user?.role }}</span><button class="user-menu__exit" type="button" title="退出登录" @click="signOut">退出</button></div>
        </div>
      </header>
      <nav v-if="workspace.tabs.length > 1" class="workspace-tabs" aria-label="工作区标签">
        <button v-for="tab in workspace.tabs" :key="tab.path" :class="['workspace-tab', { active: route.path === tab.path }]" type="button" @click="router.push(tab.path)">
          <span>{{ tab.label }}</span><span v-if="!tab.pinned" class="workspace-tab__close" aria-label="关闭标签" @click.stop="closeTab(tab.path)">×</span>
        </button>
      </nav>
      <section class="page-scroll"><router-view v-slot="{ Component }"><KeepAlive><component :is="Component" /></KeepAlive></router-view></section>
    </main>
  </div>
</template>

<style scoped>
.sidebar-dot { box-shadow: none; }
.status-dot--warning { background: var(--nc-warning); }
.status-dot--danger { background: var(--nc-danger); }
.status-dot--unknown { background: var(--nc-text-muted); }
</style>
