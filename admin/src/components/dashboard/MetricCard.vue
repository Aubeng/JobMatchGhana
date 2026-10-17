<script setup lang="ts">
import { computed } from 'vue'
import type { Metric } from '@/api/types'
import { decimal, percent } from '@/utils/format'

type Unit = 'percent' | 'turns' | 'seconds'

const props = defineProps<{ label: string; metric: Metric; unit: Unit }>()

function show(value: number | null): string {
  if (props.unit === 'percent') return percent(value)
  if (props.unit === 'seconds') return value == null ? '—' : `${decimal(value, 1)} s`
  return decimal(value, 1)
}

const value = computed(() => show(props.metric.value))
const target = computed(() => `${props.metric.higherIsBetter ? '≥' : '≤'} ${show(props.metric.target)}`)
const status = computed(() => {
  if (props.metric.meetsTarget === null) return { label: 'No data yet', cls: 'badge' }
  return props.metric.meetsTarget
    ? { label: 'Meets target', cls: 'badge badge-ok' }
    : { label: 'Below target', cls: 'badge badge-warn' }
})
</script>

<template>
  <article class="metric card">
    <header class="metric-head">
      <h3 class="metric-label">{{ label }}</h3>
      <span :class="status.cls">{{ status.label }}</span>
    </header>
    <p class="metric-value">{{ value }}</p>
    <p class="metric-target">Target {{ target }} · n = {{ metric.n }}</p>
    <p class="metric-note">{{ metric.note }}</p>
  </article>
</template>

<style scoped>
.metric { display: grid; gap: 4px; align-content: start; }
.metric-head { display: flex; justify-content: space-between; gap: 8px; align-items: start; }
.metric-label { margin: 0; font-size: 14px; color: var(--text-muted); font-weight: 600; }
.metric-value { margin: 4px 0 0; font-size: 30px; font-weight: 800; line-height: 1.1; }
.metric-target { margin: 0; font-size: 13px; font-weight: 600; }
.metric-note { margin: 4px 0 0; font-size: 12.5px; color: var(--text-muted); }
</style>
