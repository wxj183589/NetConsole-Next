<script setup lang="ts">
import { onMounted } from 'vue'
import { useAuthStore } from './stores/auth'

const auth = useAuthStore()
onMounted(() => auth.hydrate())
</script>

<template>
  <div class="app-root" :class="{ 'app-root--loading': !auth.ready }">
    <div v-if="!auth.ready" class="boot-screen"><div class="brand-mark">NC</div><p>正在连接 NetConsole Next 服务…</p></div>
    <router-view v-else />
  </div>
</template>

