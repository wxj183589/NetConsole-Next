import { describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useWorkspaceStore } from './workspace'

describe('NetConsole workspace tabs', () => {
  it('keeps independent routes in persistent tabs and closes the active route', () => {
    setActivePinia(createPinia())
    const workspace = useWorkspaceStore()
    workspace.open('/devices', '设备管理')
    workspace.open('/terminal', '终端控制台')
    expect(workspace.tabs.map((tab) => tab.path)).toEqual(['/', '/devices', '/terminal'])
    expect(workspace.activePath).toBe('/terminal')
    expect(workspace.close('/terminal')).toBe('/devices')
    expect(workspace.tabs.map((tab) => tab.path)).toEqual(['/', '/devices'])
  })
})

