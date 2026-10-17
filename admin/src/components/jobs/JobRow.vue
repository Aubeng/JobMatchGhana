<script setup lang="ts">
import { computed } from 'vue'
import type { JobSummary } from '@/api/types'
import { decimal } from '@/utils/format'

const props = defineProps<{ job: JobSummary; busy: boolean }>()
const emit = defineEmits<{ publish: [job: JobSummary]; close: [job: JobSummary] }>()

const statusClass = computed(() => ({
  badge: true,
  'badge-ok': props.job.status === 'Open',
  'badge-warn': props.job.status === 'Draft'
}))
const rating = computed(() => (props.job.averageRating == null ? '—' : `${decimal(props.job.averageRating, 1)} / 5`))
</script>

<template>
  <tr>
    <td>
      <RouterLink :to="{ name: 'job-edit', params: { id: job.id } }" class="row-title">{{ job.title }}</RouterLink>
      <div class="row-meta">
        {{ job.trade ?? '—' }}
        <span v-if="job.isSynthetic" class="badge" title="Invented demo data: never a study result">synthetic</span>
      </div>
    </td>
    <td>{{ job.area }}<div class="row-meta">{{ job.district }}</div></td>
    <td><span :class="statusClass">{{ job.status }}</span></td>
    <td class="num">{{ job.matches }}</td>
    <td class="num">{{ job.interested }}</td>
    <td class="num">{{ rating }}</td>
    <td class="row-actions">
      <button v-if="job.status !== 'Open'" type="button" class="btn btn-small btn-primary" :disabled="busy" @click="emit('publish', job)">
        Publish
      </button>
      <button v-if="job.status === 'Open'" type="button" class="btn btn-small btn-danger" :disabled="busy" @click="emit('close', job)">
        Close
      </button>
    </td>
  </tr>
</template>

<style scoped>
.row-title { font-weight: 700; text-decoration: none; }
.row-title:hover { text-decoration: underline; }
.row-meta { color: var(--text-muted); font-size: 13px; display: flex; gap: 6px; align-items: center; }
.num { text-align: right; font-variant-numeric: tabular-nums; }
.row-actions { white-space: nowrap; text-align: right; }
</style>
