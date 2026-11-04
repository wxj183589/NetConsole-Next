# 任务中心验收

状态：PASS（API/UI本地）。`/api/system/tasks/{runId}/commands` 返回命令级状态、Raw/parse 状态和错误；任务页面按业务语言显示并可打开命令明细。取消、重试、恢复由既有服务端接口继续负责。浏览器 service-backed gate 仍受已有 Mutex 影响。
