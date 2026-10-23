<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { ApiRequestError } from '../api/client'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const username = ref('admin')
const password = ref('')
const error = ref('')

async function submit(): Promise<void> {
  error.value = ''
  try { await auth.signIn(username.value.trim(), password.value); await router.push(String(route.query.redirect || '/')) }
  catch (cause) { error.value = cause instanceof ApiRequestError && cause.status === 429 ? '登录尝试过于频繁，请稍后再试' : '用户名或密码不正确'; ElMessage.error(error.value) }
}
</script>

<template>
  <main class="login-screen">
    <section class="login-card">
      <div class="login-card__brand"><div class="brand-mark">NC</div><div><h1>NetConsole Next</h1><p>网络设备采集与诊断平台</p></div></div>
      <form class="login-form" @submit.prevent="submit">
        <label>用户名<input v-model="username" autocomplete="username" autofocus required /></label>
        <label>密码<input v-model="password" type="password" autocomplete="current-password" required /></label>
        <el-button type="primary" native-type="submit" :loading="auth.loading" style="width: 100%; height: 38px">登录工作台</el-button>
      </form>
      <div v-if="error" class="login-hint" style="color: var(--nc-danger)">{{ error }}</div>
      <div class="login-hint">首次部署会幂等创建 admin / superadmin 两个角色。会话使用 HttpOnly Cookie；本页面不会读取或传输 LocalManager API Token。</div>
    </section>
  </main>
</template>

