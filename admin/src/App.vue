<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppNav from '@/components/layout/AppNav.vue'
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const showNav = computed(() => auth.isSignedIn && !route.meta.public)

async function signOut(): Promise<void> {
  await auth.signOut()
  await router.push({ name: 'login' })
}
</script>

<template>
  <div class="app">
    <AppNav v-if="showNav" :email="auth.email" @sign-out="signOut" />
    <main class="app-main">
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.app { min-height: 100vh; display: flex; flex-direction: column; }
.app-main { flex: 1; width: 100%; max-width: 1200px; margin: 0 auto; padding: 24px 16px 48px; }
</style>
