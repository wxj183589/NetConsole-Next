import { describe, expect, it } from 'vitest'
import { categoriesForVendor, subtypesFor, subtypeByBackendType } from './deviceCatalog'

describe('device catalog', () => {
  it('keeps H3C cascading definitions outside the form component', () => {
    expect(categoriesForVendor('H3C').map(category => category.label)).toEqual(['网络设备'])
    expect(subtypesFor('H3C', 'NETWORK_DEVICE').map(subtype => subtype.label)).toEqual(['无线控制器', '交换机', '路由器', '无线 AP', '车载 CPE/MR'])
  })

  it('marks FIT-AP creation as restricted instead of inventing an association', () => {
    expect(subtypeByBackendType('H3C_WIRELESS_AP')?.creation).toBe('RESTRICTED')
  })
})
