import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'
import { ApiRequestError, getSites, type Site } from '../api/client'

const STORAGE_KEY = 'netconsole-current-site'

export const useSiteContextStore = defineStore('siteContext', () => {
  const sites = ref<Site[]>([])
  const currentSiteId = ref(localStorage.getItem(STORAGE_KEY) || '')
  const loading = ref(false)
  const unavailable = ref(false)
  const error = ref('')
  const currentSite = computed(() => sites.value.find(site => site.id === currentSiteId.value) || null)
  const activeSites = computed(() => sites.value.filter(site => site.status === 'ACTIVE'))

  async function refresh(): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      sites.value = await getSites()
      unavailable.value = false
      const saved = sites.value.find(site => site.id === currentSiteId.value && site.status === 'ACTIVE')
      if (!saved) currentSiteId.value = activeSites.value[0]?.id || sites.value[0]?.id || ''
    } catch (cause) {
      sites.value = []
      unavailable.value = cause instanceof ApiRequestError && cause.status === 403
      error.value = cause instanceof Error ? cause.message : '局点列表读取失败'
    } finally { loading.value = false }
  }

  function selectSite(id: string): void {
    if (!sites.value.some(site => site.id === id && site.status === 'ACTIVE')) return
    currentSiteId.value = id
  }

  watch(currentSiteId, value => {
    if (value) localStorage.setItem(STORAGE_KEY, value)
    else localStorage.removeItem(STORAGE_KEY)
  })

  return { sites, activeSites, currentSiteId, currentSite, loading, unavailable, error, refresh, selectSite }
})
