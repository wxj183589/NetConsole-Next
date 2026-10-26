<script setup lang="ts">
withDefaults(defineProps<{ title: string; description?: string; eyebrow?: string }>(), {
  description: '',
  eyebrow: 'NetConsole Next 工作台',
})
</script>

<template>
  <section class="page">
    <header class="page-header">
      <div class="page-title-group">
        <span class="page-eyebrow">{{ eyebrow }}</span>
        <h1>{{ title }}</h1>
        <p v-if="description">{{ description }}</p>
      </div>
      <div class="page-actions"><slot name="actions" /></div>
    </header>
    <slot />
  </section>
</template>
