export const TASK_STATE_LABELS: Record<string, string> = {
  QUEUED: '排队中',
  CONNECTING: '连接中',
  RUNNING: '执行中',
  SUCCEEDED: '已完成',
  FAILED: '失败',
  PARTIAL: '部分完成',
  CANCELLED: '已取消',
  TIMED_OUT: '超时',
  INTERRUPTED: '服务重启中断',
}

export const TASK_TYPE_LABELS: Record<string, string> = {
  H3C_SSH_READONLY: '设备采集',
  SIMULATED_DEVICE: '设备操作',
  ZTE_READONLY: '设备采集',
}

export function taskStateLabel(state: string): string {
  return TASK_STATE_LABELS[state] || state || '未知状态'
}

export function taskTypeLabel(taskType: string): string {
  return TASK_TYPE_LABELS[taskType] || taskType || '设备任务'
}

export function captureStatusLabel(status?: string | null): string {
  return ({ COMPLETE: '原始数据完整', PARTIAL: '原始数据不完整' } as Record<string, string>)[status || ''] || (status ? '原始数据待核对' : '暂无原始数据')
}

export function parseStatusLabel(status?: string | null): string {
  return ({ PARSED: '已识别', EMPTY: '无可识别内容', UNRECOGNIZED: '格式未识别', PARTIAL: '识别不完整' } as Record<string, string>)[status || ''] || (status ? '识别状态待核对' : '未开始识别')
}

export function taskFailureLabel(reason?: string | null): string {
  if (!reason) return '—'
  if (reason.includes('REVOKED') || reason.includes('SCOPE')) return '执行范围或权限发生变化，任务已停止'
  if (reason.includes('Authentication') || reason.includes('AUTH')) return '账号认证失败，请检查设备连接资料'
  if (reason.includes('Timeout') || reason.includes('TIMED_OUT')) return '设备响应超时'
  if (reason.includes('HostKey') || reason.includes('HOST_KEY')) return '设备指纹发生变化，连接未继续'
  if (reason.includes('Connection') || reason.includes('CONNECT')) return '设备当前不可达或连接失败'
  if (reason.includes('ResultPersistence')) return '结果保存失败，原始证据已保留待修复'
  return `任务失败：${reason}`
}
