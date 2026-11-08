import { request } from './client'

export interface ConfigurationStorage {
  profileId: string
  manifestCount: number
  profileStoredBytes: number
  maximumBackupsPerProfile: number
  maximumStoredBytes: number
  sharedStoredBytes: number | null
  availableDiskBytes: number
  profileLimitReached: boolean
  sharedLimitReached: boolean | null
  measuredMs: number
  retentionPolicy: string
  automaticDeletion: boolean
}
export function getConfigurationStorage(profileId: string): Promise<ConfigurationStorage> {
  return request('/api/system/simulator-config/storage?profileId=' + encodeURIComponent(profileId))
}
