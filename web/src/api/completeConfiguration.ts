import { request, type ConfigBackupBatch, type ConfigBackupSchedule } from './client'

export interface CompleteConfigurationTargetPreview {
  selectionKind: 'PROFILES' | 'SITE' | 'DOMAIN'
  selectionId?: string | null
  profileIds: string[]
  items: Array<{ profileId: string; deviceId: string; name: string; siteId: string; site: string; model: string; address: string }>
  resolvedMs: number
}
export type CompleteConfigurationBatch = Omit<ConfigBackupBatch, 'configurationKind'> & { configurationKind: 'COMPLETE_RUNNING' }
export type CompleteConfigurationSchedule = Omit<ConfigBackupSchedule, 'configurationKind'> & { configurationKind: 'COMPLETE_RUNNING' }
export function resolveCompleteConfigurationTargets(selection: { profileIds?: string[]; siteId?: string; domainId?: string }): Promise<CompleteConfigurationTargetPreview> {
  return request('/api/system/complete-configuration/targets', { method: 'POST', body: JSON.stringify(selection) })
}
export function createCompleteConfigurationBatch(profileIds: string[]): Promise<CompleteConfigurationBatch> {
  return request('/api/system/config-backups', { method: 'POST', body: JSON.stringify({ profileIds, configurationKind: 'COMPLETE_RUNNING' }) })
}
export function getCompleteConfigurationBatch(id: string): Promise<CompleteConfigurationBatch> {
  return request('/api/system/config-backups/batches/' + encodeURIComponent(id))
}
export async function getCompleteConfigurationSchedules(): Promise<CompleteConfigurationSchedule[]> {
  const plans = await request<Array<ConfigBackupSchedule | CompleteConfigurationSchedule>>('/api/system/config-backup-schedules')
  return plans.filter((item): item is CompleteConfigurationSchedule => item.configurationKind === 'COMPLETE_RUNNING')
}
export function createCompleteConfigurationSchedule(profileIds: string[], intervalMinutes: number): Promise<CompleteConfigurationSchedule> {
  return request('/api/system/config-backup-schedules', { method: 'POST', body: JSON.stringify({ profileIds, configurationKind: 'COMPLETE_RUNNING', intervalMinutes }) })
}

export function runCompleteConfigurationSchedule(id: string): Promise<CompleteConfigurationSchedule> {
  return request('/api/system/config-backup-schedules/' + encodeURIComponent(id) + '/run', { method: 'POST' })
}
