import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import AppShell from './components/AppShell.vue'
import LoginView from './views/LoginView.vue'
import RouteErrorView from './views/RouteErrorView.vue'
import { lastNavigationError } from './navigationState'
import { moduleRegistry, moduleToRouteRecord } from './moduleRegistry'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: LoginView, meta: { public: true } },
    {
      path: '/', component: AppShell,
      children: [
        ...moduleRegistry.map(moduleToRouteRecord),
        { path: 'navigation-error', component: RouteErrorView, name: 'navigation-error', meta: { label: '导航错误' } },
        { path: ':pathMatch(.*)*', component: RouteErrorView, name: 'not-found', meta: { label: '页面不存在' } },
      ],
    },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.hydrate()
  if (to.meta.public && auth.isAuthenticated) return '/'
  if (!to.meta.public && !auth.isAuthenticated) return { path: '/login', query: { redirect: to.fullPath } }
  if (to.name !== 'navigation-error') lastNavigationError.value = null
  return true
})

router.onError((error, to) => {
  lastNavigationError.value = { path: to?.fullPath || window.location.pathname, message: error instanceof Error ? error.message : String(error), at: Date.now() }
  if (to?.name !== 'navigation-error') void router.replace({ name: 'navigation-error', query: { path: to?.fullPath || window.location.pathname } })
})

export default router

