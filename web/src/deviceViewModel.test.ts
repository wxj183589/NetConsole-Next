import { describe, expect, it } from 'vitest'
import { displayValue, formatManagementAddress, normalizeDevice } from './deviceViewModel'

describe('device view model', () => {
  it('never renders undefined, null, object strings or a dangling address colon', () => {
    expect(displayValue(undefined)).toBe('—')
    expect(displayValue(null)).toBe('—')
    expect(displayValue('[object Object]')).toBe('—')
    expect(formatManagementAddress('', 22)).toBe('—')
    expect(normalizeDevice({ id: '', managementAddress: '', port: 0, connectionStatus: undefined })).toMatchObject({
      id: '—', name: '未命名设备', address: '—', status: 'UNKNOWN',
    })
  })

  it('preserves valid values and maps the device quality metadata', () => {
    expect(normalizeDevice({
      id: 'switch-a', name: 'switch-a', managementAddress: '192.0.2.10', port: 22,
      username: 'readonly', deviceType: 'H3C 终端配置', protocol: 'SSH', site: '测试局点', group: '核心',
      enabled: true, credentialConfigured: false, connectionStatus: 'UNKNOWN', isValid: true, qualityIssues: [],
    })).toMatchObject({ address: '192.0.2.10:22', protocol: 'SSH', enabled: true, credentialConfigured: false })
  })
})
