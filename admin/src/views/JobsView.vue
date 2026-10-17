<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import JobStatusFilter from '@/components/jobs/JobStatusFilter.vue'
import JobTable from '@/components/jobs/JobTable.vue'
import { useJobs } from '@/composables/useJobs'

const { status, jobs, loading, error, reload, notice, actionError, busyId, publish, close } = useJobs()

const empty = computed(() => (jobs.value && jobs.value.length === 0 ? 'No jobs here yet.' : null))
</script>

<template>
  <PageHeader
    title="Jobs"
    subtitle="Publishing a job opens it for matching and sends a WhatsApp alert to suitable workers nearby (at most one alert per worker per day)."
  >
    <template #actions>
      <RouterLink :to="{ name: 'job-new' }" class="btn btn-primary">New job</RouterLink>
    </template>
  </PageHeader>

  <div class="toolbar">
    <JobStatusFilter v-model="status" />
  </div>

  <p v-if="notice" class="notice" role="status">{{ notice }}</p>
  <p v-if="actionError" class="notice notice-error" role="alert">{{ actionError }}</p>

  <StatusMessage :loading="loading && !jobs" :error="error" :empty="empty" @retry="reload" />
  <JobTable v-if="jobs && jobs.length" :jobs="jobs" :busy-id="busyId" @publish="publish" @close="close" />
</template>

<style scoped>
.toolbar { margin-bottom: 14px; }
.notice { padding: 10px 14px; border-radius: var(--radius); background: var(--ok-soft); color: var(--ok); margin: 0 0 14px; }
.notice-error { background: var(--danger-soft); color: var(--danger); }
</style>
