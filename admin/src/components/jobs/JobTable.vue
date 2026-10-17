<script setup lang="ts">
import type { JobSummary } from '@/api/types'
import JobRow from './JobRow.vue'

defineProps<{ jobs: JobSummary[]; busyId: number | null }>()
const emit = defineEmits<{ publish: [job: JobSummary]; close: [job: JobSummary] }>()
</script>

<template>
  <div class="card table-card">
    <div class="table-scroll">
      <table class="table">
        <thead>
          <tr>
            <th scope="col">Job</th>
            <th scope="col">Area</th>
            <th scope="col">Status</th>
            <th scope="col" class="num">Matched</th>
            <th scope="col" class="num">Interested</th>
            <th scope="col" class="num">Rating</th>
            <th scope="col"><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <JobRow
            v-for="job in jobs"
            :key="job.id"
            :job="job"
            :busy="busyId === job.id"
            @publish="emit('publish', $event)"
            @close="emit('close', $event)"
          />
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.table-card { padding: 0; overflow: hidden; }
.num { text-align: right; }
</style>
