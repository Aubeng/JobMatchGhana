<script setup lang="ts">
import { computed } from 'vue'
import type { AnnotationSummary } from '@/api/types'
import MetricCard from '@/components/dashboard/MetricCard.vue'
import { humanise, percent } from '@/utils/format'

const props = defineProps<{ summary: AnnotationSummary }>()

const progressWidth = computed(() => {
  const { annotated, target } = props.summary.progress
  return `${Math.min(100, target === 0 ? 0 : Math.round((annotated / target) * 100))}%`
})
const perIntent = computed(() =>
  props.summary.perIntent.map((row) => ({ ...row, accuracy: percent(row.annotated ? row.correct / row.annotated : null) }))
)
</script>

<template>
  <section class="summary" aria-labelledby="summary-title">
    <h2 id="summary-title" class="visually-hidden">Coding results</h2>
    <div class="card progress">
      <p class="progress-text">
        <strong>{{ summary.progress.annotated }}</strong> of {{ summary.progress.target }} coded
        (20% sample of {{ summary.progress.eligible }} model-analysed messages)
      </p>
      <div class="progress-track" role="progressbar" :aria-valuenow="summary.progress.annotated"
        aria-valuemin="0" :aria-valuemax="summary.progress.target">
        <span class="progress-bar" :style="{ width: progressWidth }" />
      </div>
    </div>

    <div class="metrics">
      <MetricCard label="Intent recognition accuracy" :metric="summary.intentAccuracy" unit="percent" />
      <MetricCard label="Entity extraction precision" :metric="summary.entityExtractionPrecision" unit="percent" />
    </div>

    <div v-if="perIntent.length" class="card table-card">
      <table class="table">
        <caption class="visually-hidden">Accuracy by coded intent</caption>
        <thead><tr><th scope="col">Coded intent</th><th scope="col">Messages</th><th scope="col">Model agreed</th></tr></thead>
        <tbody>
          <tr v-for="row in perIntent" :key="row.intent">
            <td>{{ humanise(row.intent) }}</td><td>{{ row.annotated }}</td><td>{{ row.accuracy }}</td>
          </tr>
        </tbody>
      </table>
      <p class="table-note">{{ summary.otherCount }} coded "none of these" (not scored).</p>
    </div>

    <div v-if="summary.confusions.length" class="card">
      <h3 class="confusions-title">Most common disagreements</h3>
      <ul class="confusions">
        <li v-for="c in summary.confusions.slice(0, 6)" :key="c.annotated + c.predicted">
          Coded <strong>{{ humanise(c.annotated) }}</strong>, model said <strong>{{ humanise(c.predicted) }}</strong>: {{ c.count }}
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.summary { display: grid; gap: 14px; align-content: start; }
.progress-text { margin: 0 0 8px; font-size: 14px; }
.progress-track { height: 10px; background: var(--surface-muted); border-radius: 999px; overflow: hidden; }
.progress-bar { display: block; height: 100%; background: var(--gold); }
.metrics { display: grid; gap: 14px; }
.table-card { padding: 0; overflow: hidden; }
.table-note { margin: 0; padding: 8px 12px; color: var(--text-muted); font-size: 13px; }
.confusions-title { margin: 0 0 8px; font-size: 15px; }
.confusions { margin: 0; padding-left: 18px; font-size: 14px; display: grid; gap: 4px; }
</style>
