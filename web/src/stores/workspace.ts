import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'

export interface WorkspaceTab { path: string; label: string; pinned?: boolean }

const labels: Record<string, string> = {
  '/': '运行总览', '/devices': '设备管理', '/network-planning': '网络规划', '/terminal': '终端控制台', '/tasks': '任务中心',
  '/wireless': '无线管理', '/rail': '轨道交通', '/tools': '工具集', '/files': '文件管理', '/settings': '系统设置',
}

export const useWorkspaceStore = defineStore('workspace', () => {
  const tabs = ref<WorkspaceTab[]>(loadTabs())
  const activePath = ref('/')
  const tabLabels = computed(() => tabs.value.map((tab) => tab.label))

  function open(path: string, label = labels[path] || '工作区'): void {
    if (!tabs.value.some((tab) => tab.path === path)) tabs.value.push({ path, label })
    activePath.value = path
  }
  function close(path: string): string {
    const index = tabs.value.findIndex((tab) => tab.path === path)
    if (index < 0 || tabs.value[index].pinned) return activePath.value
    tabs.value.splice(index, 1)
    const next = tabs.value[Math.min(index, tabs.value.length - 1)]?.path || '/'
    if (activePath.value === path) activePath.value = next
    return activePath.value
  }
  watch(tabs, (value) => sessionStorage.setItem('netconsole-workspace-tabs', JSON.stringify(value)), { deep: true })
  return { tabs, activePath, tabLabels, open, close }
})

function loadTabs(): WorkspaceTab[] {
  try {
    const value = JSON.parse(sessionStorage.getItem('netconsole-workspace-tabs') || '[]')
    if (Array.isArray(value) && value.length) return value.filter((tab) => tab && typeof tab.path === 'string')
  } catch { /* first run */ }
  return [{ path: '/', label: '运行总览', pinned: true }]
}

