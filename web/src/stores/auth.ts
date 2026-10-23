import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { ApiRequestError, getCsrf, getCurrentUser, login, logout, type WebUser } from '../api/client'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<WebUser | null>(null)
  const ready = ref(false)
  const loading = ref(false)

  const isAuthenticated = computed(() => user.value !== null)
  const isSuperAdmin = computed(() => user.value?.role === 'SUPERADMIN')

  async function hydrate(): Promise<void> {
    if (ready.value) return
    try {
      user.value = await getCurrentUser()
      await getCsrf()
    } catch (error) {
      if (!(error instanceof ApiRequestError) || error.status !== 401) console.warn('auth bootstrap failed', error)
      user.value = null
    } finally { ready.value = true }
  }

  async function signIn(username: string, password: string): Promise<void> {
    loading.value = true
    try { user.value = (await login(username, password)).user } finally { loading.value = false }
  }

  async function signOut(): Promise<void> {
    try { await logout() } finally { user.value = null }
  }

  return { user, ready, loading, isAuthenticated, isSuperAdmin, hydrate, signIn, signOut }
})

