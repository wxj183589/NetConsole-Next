<script setup lang="ts">
withDefaults(defineProps<{ title: string; description?: string }>(), { description: '' })
</script>
<template>
  <section class="page">
    <header class="page-header"><div><h1>{{ title }}</h1><p>{{ description }}</p></div><div class="page-actions"><slot name="actions" /></div></header>
    <slot />
  </section>
</template>

