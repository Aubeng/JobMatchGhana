import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// Served by the .NET API at /admin (same origin: the strict session cookie just works).
// `npm run dev` proxies /api to the API's http profile (launchSettings.json, port 5154).
export default defineConfig({
  base: '/admin/',
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) }
  },
  server: {
    port: 5173,
    proxy: { '/api': { target: 'http://localhost:5154', changeOrigin: false } }
  },
  build: {
    outDir: '../src/JobMatch.Api/wwwroot/admin',
    emptyOutDir: true
  },
  test: {
    environment: 'jsdom'
  }
})
