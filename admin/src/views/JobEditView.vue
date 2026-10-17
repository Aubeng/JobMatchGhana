<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import JobForm from '@/components/jobs/JobForm.vue'
import { useJobEditor } from '@/composables/useJobEditor'
import { useReference } from '@/composables/useReference'
import type { JobInput } from '@/api/types'

const props = defineProps<{ id?: string }>()
const router = useRouter()

const jobId = computed(() => (props.id ? Number(props.id) : null))
const reference = useReference()
const { job, loading, loadError, saving, fieldErrors, saveError, save } = useJobEditor(jobId)

const title = computed(() => (jobId.value == null ? 'New job' : 'Edit job'))
const subtitle = computed(() =>
  jobId.value == null
    ? 'New jobs are saved as drafts. Publish from the job list to open them for matching.'
    : job.value?.isSynthetic
      ? 'This is a synthetic demo job: matches against it are never study results.'
      : `Status: ${job.value?.status ?? '…'}`
)

async function submit(input: JobInput): Promise<void> {
  if (await save(input)) await router.push({ name: 'jobs' })
}
</script>

<template>
  <PageHeader :title="title" :subtitle="subtitle" />
  <StatusMessage
    :loading="(loading && !job) || (reference.loading.value && !reference.data.value)"
    :error="loadError ?? reference.error.value"
    @retry="reference.reload"
  />
  <p v-if="saveError" class="save-error" role="alert">{{ saveError }}</p>
  <JobForm
    v-if="reference.data.value && (jobId == null || job)"
    :reference="reference.data.value"
    :initial="job"
    :errors="fieldErrors"
    :saving="saving"
    @submit="submit"
    @cancel="router.push({ name: 'jobs' })"
  />
</template>

<style scoped>
.save-error { padding: 10px 14px; border-radius: var(--radius); background: var(--danger-soft); color: var(--danger); }
</style>
