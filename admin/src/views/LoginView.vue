<script setup lang="ts">
import { shallowRef } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LoginForm from '@/components/auth/LoginForm.vue'
import { useAuthStore } from '@/stores/auth'
import { ApiError } from '@/api/client'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const busy = shallowRef(false)
const error = shallowRef<string | null>(null)

async function signIn(credentials: { email: string; password: string }): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await auth.signIn(credentials.email, credentials.password)
    const next = typeof route.query.next === 'string' && route.query.next.startsWith('/') ? route.query.next : '/'
    await router.replace(next)
  } catch (e) {
    error.value = e instanceof ApiError ? e.message : 'Sign-in failed. Check your connection.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login-page">
    <LoginForm :busy="busy" :error="error" @submit="signIn" />
  </div>
</template>

<style scoped>
.login-page { min-height: 70vh; display: grid; place-items: center; }
</style>
