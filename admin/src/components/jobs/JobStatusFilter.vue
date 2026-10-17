<script setup lang="ts">
import type { JobStatus } from '@/api/types'

const status = defineModel<JobStatus | ''>({ required: true })

const options: { value: JobStatus | ''; label: string }[] = [
  { value: '', label: 'All' },
  { value: 'Draft', label: 'Drafts' },
  { value: 'Open', label: 'Open' },
  { value: 'Closed', label: 'Closed' }
]
</script>

<template>
  <div class="filter" role="group" aria-label="Filter by status">
    <button
      v-for="option in options"
      :key="option.label"
      type="button"
      class="filter-option"
      :aria-pressed="status === option.value"
      @click="status = option.value"
    >
      {{ option.label }}
    </button>
  </div>
</template>

<style scoped>
.filter { display: inline-flex; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; background: var(--surface); }
.filter-option { border: 0; background: transparent; padding: 6px 14px; font: inherit; cursor: pointer; color: var(--text-muted); }
.filter-option + .filter-option { border-left: 1px solid var(--border); }
.filter-option[aria-pressed="true"] { background: var(--accent-soft); color: var(--accent-strong); font-weight: 700; }
</style>
