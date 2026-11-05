import type { RouteRecordSingleView, RouteRecordRaw } from 'vue-router'
import {
  Connection, DataLine, FolderOpened, House, List, Monitor, Setting, Share, Tools, TrendCharts,
} from '@element-plus/icons-vue'

export type ModuleStatus = 'READY' | 'DISABLED'

export interface ModuleDefinition {
  key: string
  path: string
  label: string
  status: ModuleStatus
  icon: typeof House
  component: NonNullable<RouteRecordRaw['component']>
  disabledReason?: string
}

/**
 * The single source for user-facing modules. Router records and shell navigation
 * are both derived from this list so a module cannot silently drift between them.
 */
export const moduleRegistry: readonly ModuleDefinition[] = [
  { key: 'dashboard', path: '/', label: '运行总览', status: 'READY', icon: House, component: () => import('./views/DashboardView.vue') },
  { key: 'devices', path: '/devices', label: '设备管理', status: 'READY', icon: Monitor, component: () => import('./views/DevicesView.vue') },
  { key: 'zte', path: '/zte', label: 'ZTE 采集', status: 'READY', icon: Connection, component: () => import('./views/ZteView.vue') },
  { key: 'h3c', path: '/h3c', label: 'H3C 事实回放', status: 'READY', icon: Connection, component: () => import('./views/H3cView.vue') },
  { key: 'config-backups', path: '/config-backups', label: '配置备份', status: 'READY', icon: FolderOpened, component: () => import('./views/ConfigBackupsView.vue') },
  { key: 'network-planning', path: '/network-planning', label: '网络规划', status: 'READY', icon: Share, component: () => import('./views/NetworkPlanningView.vue') },
  { key: 'terminal', path: '/terminal', label: '终端控制台', status: 'READY', icon: Connection, component: () => import('./views/TerminalView.vue') },
  { key: 'tasks', path: '/tasks', label: '任务中心', status: 'READY', icon: List, component: () => import('./views/TasksView.vue') },
  { key: 'wireless', path: '/wireless', label: '无线业务', status: 'READY', icon: TrendCharts, component: () => import('./views/WirelessView.vue') },
  {
    key: 'rail', path: '/rail', label: '轨道交通', status: 'DISABLED', icon: DataLine,
    component: () => import('./views/DisabledView.vue'), disabledReason: '轨道交通模块尚未接入真实业务 API',
  },
  { key: 'tools', path: '/tools', label: '工具集', status: 'READY', icon: Tools, component: () => import('./views/ToolsView.vue') },
  { key: 'files', path: '/files', label: '文件管理', status: 'READY', icon: FolderOpened, component: () => import('./views/FilesView.vue') },
  { key: 'settings', path: '/settings', label: '系统设置', status: 'READY', icon: Setting, component: () => import('./views/SettingsView.vue') },
]

export function moduleToRouteRecord(module: ModuleDefinition): RouteRecordSingleView {
  return {
    path: module.path === '/' ? '' : module.path.slice(1),
    component: module.component,
    name: module.key,
    meta: { label: module.label, moduleKey: module.key, moduleStatus: module.status },
  }
}

export function moduleForPath(path: string): ModuleDefinition | undefined {
  return moduleRegistry.find((module) => module.path === path)
}
