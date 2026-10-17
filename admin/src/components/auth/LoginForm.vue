<script setup lang="ts">
import { shallowRef } from 'vue'

defineProps<{ busy: boolean; error: string | null }>()
const emit = defineEmits<{ submit: [credentials: { email: string; password: string }] }>()

const email = shallowRef('')
const password = shallowRef('')

function onSubmit(): void {
  emit('submit', { email: email.value.trim(), password: password.value })
}
</script>

<template>
  <form class="login card" @submit.prevent="onSubmit">
    <h1 class="login-title">JobMatch Ghana</h1>
    <p class="login-sub">BOOST administrator sign-in</p>

    <label class="field">
      <span class="field-label">Email</span>
      <input v-model="email" class="input" type="email" autocomplete="username" required />
    </label>
    <label class="field">
      <span class="field-label">Password</span>
      <input v-model="password" class="input" type="password" autocomplete="current-password" required />
    </label>

    <p v-if="error" class="field-error" role="alert">{{ error }}</p>
    <button type="submit" class="btn btn-primary login-button" :disabled="busy">
      {{ busy ? 'Signing in…' : 'Sign in' }}
    </button>
  </form>
</template>

<style scoped>
.login { display: grid; gap: 14px; width: 100%; max-width: 380px; padding: 28px; }
.login-title { margin: 0; font-size: 22px; color: var(--accent-strong); }
.login-sub { margin: -8px 0 4px; color: var(--text-muted); }
.login-button { justify-content: center; }
</style>
