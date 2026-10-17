<script setup lang="ts">
import { computed } from 'vue'
import type { Funnel } from '@/api/types'

const props = defineProps<{ funnel: Funnel }>()

const steps = computed(() => {
  const f = props.funnel
  const max = Math.max(f.consented, 1)
  return [
    { label: 'Consented', value: f.consented },
    { label: 'Profile confirmed', value: f.profilesConfirmed },
    { label: 'Received matches', value: f.matched },
    { label: 'Rated matches', value: f.rated },
    { label: 'Interested taps', value: f.interested },
    { label: 'Connected by staff', value: f.connected }
  ].map((s) => ({ ...s, width: `${Math.round((s.value / max) * 100)}%` }))
})
</script>

<template>
  <section class="funnel card" aria-labelledby="funnel-title">
    <h2 id="funnel-title" class="funnel-title">Worker journey</h2>
    <ol class="funnel-list">
      <li v-for="step in steps" :key="step.label" class="funnel-step">
        <span class="funnel-label">{{ step.label }}</span>
        <span class="funnel-track"><span class="funnel-bar" :style="{ width: step.width }" /></span>
        <span class="funnel-value">{{ step.value }}</span>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.funnel-title { margin: 0 0 12px; font-size: 17px; }
.funnel-list { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
.funnel-step { display: grid; grid-template-columns: 160px 1fr 48px; gap: 12px; align-items: center; }
.funnel-label { font-size: 14px; }
.funnel-track { height: 12px; background: var(--surface-muted); border-radius: 999px; overflow: hidden; }
.funnel-bar { display: block; height: 100%; background: var(--accent); border-radius: 999px; min-width: 2px; }
.funnel-value { font-weight: 700; text-align: right; }
@media (max-width: 560px) {
  .funnel-step { grid-template-columns: 1fr 48px; }
  .funnel-track { grid-column: 1 / -1; order: 3; }
}
</style>
