<script setup lang="ts">
defineProps<{ email: string | null }>()
const emit = defineEmits<{ signOut: [] }>()

const links = [
  { to: { name: 'dashboard' }, label: 'Dashboard', exact: true },
  { to: { name: 'jobs' }, label: 'Jobs', exact: false },
  { to: { name: 'follow-ups' }, label: 'Follow-ups', exact: false },
  { to: { name: 'annotate' }, label: 'Annotate', exact: false },
  { to: { name: 'supply' }, label: 'Skills supply', exact: false }
]
</script>

<template>
  <header class="nav">
    <div class="nav-inner">
      <RouterLink :to="{ name: 'dashboard' }" class="nav-brand">JobMatch <span class="nav-brand-sub">Ghana admin</span></RouterLink>
      <nav aria-label="Main" class="nav-links">
        <!-- "/" is a prefix of every route, so the dashboard highlights on an exact match only. -->
        <RouterLink v-for="link in links" :key="link.label" :to="link.to" class="nav-link"
          :active-class="link.exact ? '' : 'nav-link-active'" exact-active-class="nav-link-active">
          {{ link.label }}
        </RouterLink>
      </nav>
      <div class="nav-user">
        <span class="nav-email">{{ email }}</span>
        <button type="button" class="btn btn-small" @click="emit('signOut')">Sign out</button>
      </div>
    </div>
  </header>
</template>

<style scoped>
.nav { background: var(--accent-strong); color: #fff; }
.nav-inner { max-width: 1200px; margin: 0 auto; padding: 10px 16px; display: flex; align-items: center; gap: 24px; flex-wrap: wrap; }
.nav-brand { color: #fff; font-weight: 800; text-decoration: none; letter-spacing: 0.2px; }
.nav-brand-sub { font-weight: 500; opacity: 0.8; }
.nav-links { display: flex; gap: 4px; flex-wrap: wrap; flex: 1; }
.nav-link { color: #fff; text-decoration: none; padding: 6px 10px; border-radius: 8px; opacity: 0.85; }
.nav-link:hover { opacity: 1; background: rgb(255 255 255 / 10%); }
.nav-link-active { opacity: 1; background: rgb(255 255 255 / 18%); font-weight: 600; }
.nav-user { display: flex; align-items: center; gap: 10px; }
.nav-email { font-size: 13px; opacity: 0.85; }
</style>
