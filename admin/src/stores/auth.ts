import { defineStore } from 'pinia'
import { shallowRef, computed } from 'vue'
import { api } from '@/api/client'

/** The signed-in admin. App-wide state (router guard, nav), hence a store. */
export const useAuthStore = defineStore('auth', () => {
  const email = shallowRef<string | null>(null)
  const checked = shallowRef(false)
  const isSignedIn = computed(() => email.value !== null)

  async function checkSession(): Promise<void> {
    try {
      email.value = (await api.get<{ email: string }>('/api/auth/me')).email
    } catch {
      email.value = null
    } finally {
      checked.value = true
    }
  }

  async function signIn(address: string, password: string): Promise<void> {
    email.value = (await api.post<{ email: string }>('/api/auth/login', { email: address, password })).email
  }

  async function signOut(): Promise<void> {
    await api.post('/api/auth/logout')
    email.value = null
  }

  function sessionExpired(): void {
    email.value = null
  }

  return { email, checked, isSignedIn, checkSession, signIn, signOut, sessionExpired }
})
