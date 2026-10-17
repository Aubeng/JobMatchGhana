<script setup lang="ts">
import { computed } from 'vue'
import type { Analytics } from '@/api/types'
import { percent } from '@/utils/format'

const props = defineProps<{ analytics: Analytics }>()

const intents = computed(() => Object.entries(props.analytics.nluIntents).sort((a, b) => b[1] - a[1]))
const rows = computed(() => {
  const a = props.analytics
  return [
    { label: 'Repair rate (turns the bot misunderstood)', value: percent(a.repairRate, 1) },
    { label: 'Human handoffs (open / all)', value: `${a.openHandoffs} / ${a.handoffCases}` },
    { label: 'NLU fallback rate (service unavailable)', value: percent(a.nluFallbackRate, 1) },
    { label: 'NLU latency, 95th percentile', value: a.nluLatencyP95Ms == null ? '—' : `${a.nluLatencyP95Ms} ms` },
    { label: 'Ratings on synthetic demo jobs (excluded)', value: String(a.syntheticMatchesRated) }
  ]
})
</script>

<template>
  <section class="ops card" aria-labelledby="ops-title">
    <h2 id="ops-title" class="ops-title">Operations</h2>
    <dl class="ops-list">
      <div v-for="row in rows" :key="row.label" class="ops-row">
        <dt>{{ row.label }}</dt>
        <dd>{{ row.value }}</dd>
      </div>
    </dl>
    <h3 class="ops-subtitle">Predicted intents (all analysed messages)</h3>
    <p v-if="intents.length === 0" class="ops-empty">No messages analysed yet.</p>
    <ul v-else class="ops-intents">
      <li v-for="[intent, count] in intents" :key="intent">
        <span class="badge badge-accent">{{ intent }}</span> {{ count }}
      </li>
    </ul>
  </section>
</template>

<style scoped>
.ops-title { margin: 0 0 8px; font-size: 17px; }
.ops-list { margin: 0; display: grid; }
.ops-row { display: flex; justify-content: space-between; gap: 12px; padding: 8px 0; border-bottom: 1px solid var(--border); }
.ops-row dt { color: var(--text-muted); }
.ops-row dd { margin: 0; font-weight: 700; }
.ops-subtitle { margin: 16px 0 8px; font-size: 14px; }
.ops-empty { color: var(--text-muted); margin: 0; }
.ops-intents { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 8px 14px; font-size: 14px; }
</style>
