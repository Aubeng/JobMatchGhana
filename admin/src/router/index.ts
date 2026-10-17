import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

export const router = createRouter({
  history: createWebHistory('/admin/'),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    { path: '/', name: 'dashboard', component: () => import('@/views/DashboardView.vue') },
    { path: '/jobs', name: 'jobs', component: () => import('@/views/JobsView.vue') },
    { path: '/jobs/new', name: 'job-new', component: () => import('@/views/JobEditView.vue') },
    { path: '/jobs/:id(\d+)', name: 'job-edit', component: () => import('@/views/JobEditView.vue'), props: true },
    { path: '/follow-ups', name: 'follow-ups', component: () => import('@/views/FollowUpsView.vue') },
    { path: '/annotate', name: 'annotate', component: () => import('@/views/AnnotateView.vue') },
    { path: '/supply', name: 'supply', component: () => import('@/views/SupplyView.vue') },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  if (!auth.checked) await auth.checkSession()
  if (!to.meta.public && !auth.isSignedIn) return { name: 'login', query: { next: to.fullPath } }
  if (to.name === 'login' && auth.isSignedIn) return { name: 'dashboard' }
})
