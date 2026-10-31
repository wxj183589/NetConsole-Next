export interface DeviceSubtypeDefinition {
  key: string
  label: string
  backendType: string
  creation: 'SUPPORTED' | 'RESTRICTED'
  note?: string
}

export interface DeviceCategoryDefinition {
  key: string
  label: string
  subtypes: DeviceSubtypeDefinition[]
}

export interface DeviceVendorDefinition {
  key: string
  label: string
  categories: DeviceCategoryDefinition[]
}

export const DEVICE_CATALOG: DeviceVendorDefinition[] = [
  {
    key: 'H3C',
    label: 'H3C',
    categories: [
      {
        key: 'NETWORK_DEVICE',
        label: '网络设备',
        subtypes: [
          { key: 'WIRELESS_CONTROLLER', label: '无线控制器', backendType: 'H3C_WIRELESS_CONTROLLER', creation: 'SUPPORTED' },
          { key: 'SWITCH', label: '交换机', backendType: 'H3C_SWITCH', creation: 'SUPPORTED' },
          { key: 'ROUTER', label: '路由器', backendType: 'H3C_ROUTER', creation: 'SUPPORTED' },
          { key: 'WIRELESS_AP', label: '无线 AP', backendType: 'H3C_WIRELESS_AP', creation: 'RESTRICTED', note: 'FIT-AP 必须先由可信 AC 关联契约确认，本页面不会猜测 AC 或 MAC。' },
          { key: 'CARBORNE_CPE_MR', label: '车载 CPE/MR', backendType: 'H3C_CARBORNE_CPE_MR', creation: 'SUPPORTED' },
        ],
      },
    ],
  },
]

export const supportedVendors = (): DeviceVendorDefinition[] => DEVICE_CATALOG
export const categoriesForVendor = (vendorKey: string): DeviceCategoryDefinition[] =>
  DEVICE_CATALOG.find(vendor => vendor.key === vendorKey)?.categories || []
export const subtypesFor = (vendorKey: string, categoryKey: string): DeviceSubtypeDefinition[] =>
  categoriesForVendor(vendorKey).find(category => category.key === categoryKey)?.subtypes || []
export const subtypeByBackendType = (backendType: string): DeviceSubtypeDefinition | undefined =>
  DEVICE_CATALOG.flatMap(vendor => vendor.categories.flatMap(category => category.subtypes)).find(subtype => subtype.backendType === backendType)
