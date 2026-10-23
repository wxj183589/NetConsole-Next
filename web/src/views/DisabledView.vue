<script setup lang="ts">
import NcLayout from '../components/NcLayout.vue'
import { useRoute } from 'vue-router'
const route = useRoute()
</script>
<template><NcLayout :title="String(route.meta.label || '模块')" description="该模块已登记导航结构，等待后续阶段接入真实后端能力"><div class="card"><div class="empty-state"><div><strong>开发中 · 未开放</strong><small>本阶段不提供模拟数据。该能力将在后续 Web PR 中建立接口契约并通过真实环境验收后启用。</small></div></div></div></NcLayout></template>

