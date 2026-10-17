import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { setUnauthorizedHandler } from './api/client'
import { useAuthStore } from './stores/auth'
import './assets/main.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)

// A 401 from any API call means the session ended: back to sign-in, then back here.
setUnauthorizedHandler(() => {
  useAuthStore().sessionExpired()
  void router.push({ name: 'login', query: { next: router.currentRoute.value.fullPath } })
})

app.mount('#app')
