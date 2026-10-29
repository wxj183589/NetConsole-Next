import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import AppShell from './components/AppShell.vue'
import LoginView from './views/LoginView.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: LoginView, meta: { public: true } },
    {
      path: '/', component: AppShell,
      children: [
        { path: '', component: () => import('./views/DashboardView.vue'), name: 'dashboard', meta: { label: '运行总览' } },
        { path: 'devices', component: () => import('./views/DevicesView.vue'), name: 'devices', meta: { label: '设备管理' } },
        { path: 'zte', component: () => import('./views/ZteView.vue'), name: 'zte', meta: { label: 'ZTE 采集' } },
        { path: 'network-planning', component: () => import('./views/NetworkPlanningView.vue'), name: 'network-planning', meta: { label: '网络规划' } },
        { path: 'terminal', component: () => import('./views/TerminalView.vue'), name: 'terminal', meta: { label: '终端控制台' } },
        { path: 'tasks', component: () => import('./views/TasksView.vue'), name: 'tasks', meta: { label: '任务中心' } },
        ...['wireless', 'rail'].map((path) => ({ path, component: () => import('./views/DisabledView.vue'), name: path, meta: { label: ({ wireless: '无线管理', rail: '轨道交通' } as Record<string, string>)[path] } })),
        { path: 'tools', component: () => import('./views/ToolsView.vue'), name: 'tools', meta: { label: '工具集' } },
        { path: 'files', component: () => import('./views/FilesView.vue'), name: 'files', meta: { label: '文件管理' } },
        { path: 'settings', component: () => import('./views/SettingsView.vue'), name: 'settings', meta: { label: '系统设置' } },
      ],
    },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.hydrate()
  if (to.meta.public && auth.isAuthenticated) return '/'
  if (!to.meta.public && !auth.isAuthenticated) return { path: '/login', query: { redirect: to.fullPath } }
  return true
})

export default router

