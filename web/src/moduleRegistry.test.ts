import { describe, expect, it } from 'vitest'
import { moduleForPath, moduleRegistry, moduleToRouteRecord } from './moduleRegistry'

describe('module registry', () => {
  it('is the unique route and navigation catalog', () => {
    expect(moduleRegistry.length).toBeGreaterThan(1)
    expect(new Set(moduleRegistry.map((module) => module.key)).size).toBe(moduleRegistry.length)
    expect(new Set(moduleRegistry.map((module) => module.path)).size).toBe(moduleRegistry.length)
    expect(moduleForPath('/devices')?.label).toBe('设备管理')
    expect(moduleToRouteRecord(moduleRegistry[0]).path).toBe('')
  })

  it('keeps disabled modules explicit and prevents fake readiness', () => {
    const rail = moduleForPath('/rail')
    expect(rail?.status).toBe('DISABLED')
    expect(rail?.disabledReason).toContain('真实业务 API')
    expect(moduleRegistry.filter((module) => module.status === 'READY').every((module) => !module.disabledReason)).toBe(true)
  })
})
