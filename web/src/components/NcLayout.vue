<script setup lang="ts">
import { computed } from 'vue'
import { useSiteContextStore } from '../stores/siteContext'

withDefaults(defineProps<{ title: string; description?: string; eyebrow?: string }>(), {
  description: '',
  eyebrow: 'NetConsole Next 工作台',
})
const siteContext = useSiteContextStore()
const contextEyebrow = computed(() => siteContext.currentSite ? `${siteContext.currentSite.name} · ${siteContext.currentSite.id}` : '当前局点未建立可信上下文')
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div class="page-title-group">
        <span class="page-eyebrow">{{ eyebrow }} · {{ contextEyebrow }}</span>
        <h1>{{ title }}</h1>
        <p v-if="description">{{ description }}</p>
      </div>
      <div class="page-actions"><slot name="actions" /></div>
    </header>
    <slot />
  </section>
</template>
