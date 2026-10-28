import type { DeviceInventoryItem } from './api/client'

export interface DeviceRow {
  id: string
  name: string
  address: string
  username: string
  vendor: string
  model: string
  type: string
  protocol: string
  site: string
  group: string
  status: string
  enabled: boolean
  credentialConfigured: boolean
  lastSeenMs?: number | null
  lastDisconnectReason?: string | null
  isValid: boolean
  qualityIssues: string[]
}

export function displayValue(value: unknown, fallback = '—'): string {
  if (value === null || value === undefined) return fallback
  const text = String(value).trim()
  if (!text || text === 'undefined' || text === 'null' || text === '[object Object]') return fallback
  return text
}

export function formatManagementAddress(host: unknown, port: unknown): string {
  const address = displayValue(host)
  if (address === '—') return address
  const numericPort = Number(port)
  return Number.isInteger(numericPort) && numericPort > 0 && numericPort <= 65535 ? `${address}:${numericPort}` : address
}

export function normalizeDevice(item: Partial<DeviceInventoryItem>): DeviceRow {
  return {
    id: displayValue(item.id),
    name: displayValue(item.name, displayValue(item.id, '未命名设备')),
    address: formatManagementAddress(item.managementAddress, item.port),
    username: displayValue(item.username),
    vendor: displayValue(item.vendor),
    model: displayValue(item.model),
    type: displayValue(item.deviceType),
    protocol: displayValue(item.protocol),
    site: displayValue(item.site),
    group: displayValue(item.group),
    status: displayValue(item.connectionStatus, 'UNKNOWN'),
    enabled: item.enabled === true,
    credentialConfigured: item.credentialConfigured === true,
    lastSeenMs: item.lastSeenMs ?? null,
    lastDisconnectReason: item.lastDisconnectReason ?? null,
    isValid: item.isValid !== false,
    qualityIssues: Array.isArray(item.qualityIssues) ? item.qualityIssues.map(value => displayValue(value, 'UNKNOWN')) : [],
  }
}
