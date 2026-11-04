import { describe, expect, it } from 'vitest'
import { captureStatusLabel, parseStatusLabel, taskFailureLabel, taskStateLabel, taskTypeLabel } from './taskViewModel'

describe('task view model', () => {
  it('uses operator friendly labels while preserving unknown states', () => {
    expect(taskTypeLabel('H3C_SSH_READONLY')).toBe('设备采集')
    expect(taskStateLabel('RUNNING')).toBe('执行中')
    expect(taskStateLabel('NEW_VENDOR_STATE')).toBe('NEW_VENDOR_STATE')
    expect(captureStatusLabel('COMPLETE')).toBe('原始数据完整')
    expect(parseStatusLabel('UNRECOGNIZED')).toBe('格式未识别')
  })

  it('maps security and connection failures without exposing exception wording as the primary message', () => {
    expect(taskFailureLabel('TASK_SCOPE_REVOKED_BEFORE_COMMAND')).toContain('权限')
    expect(taskFailureLabel('H3cAuthenticationFailedException')).toContain('认证失败')
    expect(taskFailureLabel('H3cConnectionTimeoutException')).toBe('设备响应超时')
    expect(taskFailureLabel(null)).toBe('—')
  })
})
