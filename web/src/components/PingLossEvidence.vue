<script setup lang="ts">
import { computed, onBeforeUnmount, onDeactivated, ref, watch } from 'vue'
import type { NetworkDiagnosticItem } from '../api/client'
import { getPingLossEvidence, type PingLossEvidenceSnapshot } from '../api/pingLossEvidence'

const props = defineProps<{ runId: string; item: NetworkDiagnosticItem; identityKey: string }>()
const lostSamples = computed(() => props.item.kind === 'LONG_PING' ? (props.item.quickPing?.samples ?? []).filter(sample =>
  ['TIMED_OUT', 'UNREACHABLE'].includes(sample.status) && sample.latencyMs === null) : [])
const selected = ref(''), result = ref<PingLossEvidenceSnapshot | null>(null), loading = ref(false), error = ref('')
let generation = 0
const time = (ms: number) => new Date(ms).toLocaleString('zh-CN', { hour12: false })
function clearResult() { ++generation; result.value = null; loading.value = false; error.value = '' }
function clear() { clearResult(); selected.value = '' }
async function load() {
  clearResult()
  const sample = lostSamples.value.find(entry => String(entry.sequence) === selected.value)
  if (!sample || !props.identityKey) return
  const current = generation, item = props.item, ping = item.quickPing!
  loading.value = true
  try {
    const next = await getPingLossEvidence(props.runId, item.id, sample.sequence)
    if (generation !== current) return
    if (next.runId !== props.runId || next.itemId !== item.id || next.deviceId !== item.deviceId || next.siteId !== item.siteId ||
      next.sequence !== sample.sequence || next.sampleCapturedMs !== sample.capturedMs || next.sampleStatus !== sample.status ||
      next.destinationAddress !== ping.destinationAddress || next.protocol !== ping.protocol || next.port !== ping.port ||
      next.connectedApState !== 'UNKNOWN' || next.facts.some(fact => fact.capturedMs > sample.capturedMs || fact.collectionEndedMs > sample.capturedMs ||
        fact.collectionEndedMs < fact.capturedMs || fact.ageMs !== sample.capturedMs - fact.capturedMs))
      throw new Error('Evidence does not match the selected historical boundary')
    result.value = next
  } catch { if (generation === current) error.value = '历史证据不可访问或与采样范围不符，请重新读取结果并核对权限。' }
  finally { if (generation === current) loading.value = false }
}
// Polling replaces item objects; invalidate only if the selected target/sample identity changes.
watch([() => props.identityKey, () => props.runId, () => props.item.id, () => props.item.deviceId, () => props.item.siteId,
  () => props.item.quickPing?.protocol, () => props.item.quickPing?.destinationAddress, () => props.item.quickPing?.port], clear, { flush: 'sync' })
watch(() => selected.value, clearResult, { flush: 'sync' })
watch(() => {
  const sample = lostSamples.value.find(entry => String(entry.sequence) === selected.value)
  return sample ? `${sample.sequence}:${sample.capturedMs}:${sample.status}` : ''
}, (sample, previous) => {
  if (previous && sample !== previous) clear()
}, { flush: 'sync' })
onDeactivated(clear)
onBeforeUnmount(clear)
</script>

<template>
  <section v-if="lostSamples.length" class="ping-loss-evidence" aria-label="Long Ping 丢包历史证据">
    <div class="loss-toolbar"><label class="form-field"><span>丢失采样</span><select v-model="selected" aria-label="选择丢失采样"><option value="">请选择</option><option v-for="sample in lostSamples" :key="sample.sequence" :value="String(sample.sequence)">#{{ sample.sequence }} · {{ time(sample.capturedMs) }} · {{ sample.status }}</option></select></label><button type="button" :disabled="!selected || loading" @click="load">{{ loading ? '正在读取历史证据' : '查看采样前证据' }}</button></div>
    <p class="muted">只读该设备和 Site 在采样完成时间之前最近的已验证采集记录。采样时间是超时或响应结束时间；接入 AP 需要客户端关联证据，时间接近不能证明关联或丢包原因。</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <div v-if="result" class="loss-result">
      <p><strong>接入 AP：未知（UNKNOWN）</strong> · 缺少权威客户端与 AP 关联记录</p>
      <p class="mono">{{ result.deviceId }} · {{ result.siteId }} · #{{ result.sequence }} · {{ time(result.sampleCapturedMs) }}</p>
      <p v-if="result.state === 'TARGET_BINDING_UNPROVEN'">目标登记已变化或缺少当时的端点证明，无法安全关联历史证据。</p>
      <p v-else-if="!result.facts.length">采样前没有可验证的原始采集证据，历史上下文未知。</p>
      <p v-if="result.hasMore" role="status">仅展示最近 20 个证据身份，范围不完整。</p>
      <article v-for="fact in result.facts" :key="fact.id" class="loss-fact">
        <strong>{{ fact.kind }} · {{ fact.identityKey }}</strong><p>采集 {{ time(fact.capturedMs) }}；采集完成 {{ time(fact.collectionEndedMs) }}；距采样 {{ fact.ageMs }} ms；记录状态 {{ fact.status }}（历史观测）</p>
        <p v-if="Object.keys(fact.observedValues).length">{{ Object.entries(fact.observedValues).map(([key, value]) => `${key}: ${value}`).join(' · ') }}</p>
        <details><summary>证据来源与原始记录引用</summary><p class="mono">证据 {{ fact.id }}<br />{{ fact.source }}<br />{{ fact.capturedScopeKey }}</p><p v-for="raw in fact.rawReferences" :key="raw.runId + raw.command" class="mono">任务 {{ raw.runId }} · {{ raw.command }}<br />输出 SHA256 {{ raw.outputSha256 }}<br />分段 SHA256 {{ raw.segmentSha256 }}</p></details>
      </article>
      <small class="muted">这些是历史观测，不代表采样时仍有效；证据发布时间未保存，不能确认当时页面已可见。未使用采样之后的证据或当前无线状态倒推历史。</small>
    </div>
  </section>
</template>

<style scoped>
.ping-loss-evidence { border-top: 1px solid var(--nc-border); margin-top: 12px; padding-top: 12px; overflow-wrap: anywhere; }
.loss-toolbar { display: flex; align-items: flex-end; flex-wrap: wrap; gap: 12px; }
.loss-toolbar .form-field { min-width: 220px; max-width: 100%; }
.loss-toolbar select { max-width: 100%; }
.loss-fact { margin-top: 12px; padding: 10px; border: 1px solid var(--nc-border); border-radius: 6px; }
.loss-fact p { margin: 6px 0; }
</style>
