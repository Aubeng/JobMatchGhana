<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import MetricCard from '@/components/dashboard/MetricCard.vue'
import FunnelBars from '@/components/dashboard/FunnelBars.vue'
import OperationsPanel from '@/components/dashboard/OperationsPanel.vue'
import ExportLinks from '@/components/dashboard/ExportLinks.vue'
import { useAnalytics } from '@/composables/useAnalytics'
import { dateTime } from '@/utils/format'

const { data, loading, error, reload } = useAnalytics()

// The S&D §4.2 quantitative criteria, in the order the dissertation reports them.
const metrics = computed(() => {
  const a = data.value
  if (!a) return []
  return [
    { label: 'Task completion rate', metric: a.taskCompletionRate, unit: 'percent' as const },
    { label: 'Conversation efficiency (turns)', metric: a.conversationEfficiency, unit: 'turns' as const },
    { label: 'Intent recognition accuracy', metric: a.intentAccuracy, unit: 'percent' as const },
    { label: 'Entity extraction precision', metric: a.entityExtractionPrecision, unit: 'percent' as const },
    { label: 'Matching relevance', metric: a.matchingRelevance, unit: 'percent' as const },
    { label: 'Response latency (p95)', metric: a.responseLatencyP95Seconds, unit: 'seconds' as const }
  ]
})
const updated = computed(() => (data.value ? `Updated ${dateTime(data.value.generatedUtc)}` : ''))
</script>

<template>
  <PageHeader
    title="Dashboard"
    subtitle="Pilot evaluation metrics against the targets set in the Specification (§4.2). Anonymised."
  >
    <template #actions>
      <span class="updated">{{ updated }}</span>
      <button type="button" class="btn" :disabled="loading" @click="reload">Refresh</button>
    </template>
  </PageHeader>

  <StatusMessage :loading="loading && !data" :error="error" @retry="reload" />

  <template v-if="data">
    <div class="metrics">
      <MetricCard v-for="m in metrics" :key="m.label" :label="m.label" :metric="m.metric" :unit="m.unit" />
    </div>
    <div class="panels">
      <FunnelBars :funnel="data.funnel" />
      <OperationsPanel :analytics="data" />
    </div>
    <ExportLinks />
  </template>
</template>

<style scoped>
.updated { align-self: center; color: var(--text-muted); font-size: 13px; }
.metrics { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 14px; margin-bottom: 14px; }
.panels { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 14px; margin-bottom: 14px; }
</style>
