<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import InterestedList from '@/components/followups/InterestedList.vue'
import HandoffList from '@/components/followups/HandoffList.vue'
import { useFollowUps } from '@/composables/useFollowUps'

const { interested, handoffs, actionError, setOutcome, resolveHandoff } = useFollowUps()

const interestedEmpty = computed(() => (interested.data.value?.length === 0 ? 'Nobody is waiting to be connected.' : null))
const handoffsEmpty = computed(() => (handoffs.data.value?.length === 0 ? 'No open handoffs.' : null))
</script>

<template>
  <PageHeader
    title="Follow-ups"
    subtitle="Workers waiting for a person. These are the only screens that show phone numbers: use them to make contact, and nothing else."
  />
  <p v-if="actionError" class="action-error" role="alert">{{ actionError }}</p>

  <section class="section" aria-labelledby="interested-title">
    <h2 id="interested-title" class="section-title">Interested in a job</h2>
    <p class="section-text">The worker tapped "I'm interested". Contact the employer and the worker, then record the outcome.</p>
    <StatusMessage :loading="interested.loading.value && !interested.data.value" :error="interested.error.value"
      :empty="interestedEmpty" @retry="interested.reload" />
    <InterestedList v-if="interested.data.value?.length" :items="interested.data.value" @outcome="setOutcome" />
  </section>

  <section class="section" aria-labelledby="handoff-title">
    <h2 id="handoff-title" class="section-title">Needs help (human handoff)</h2>
    <p class="section-text">The bot failed to understand this worker three times in a row and told them someone would help.</p>
    <StatusMessage :loading="handoffs.loading.value && !handoffs.data.value" :error="handoffs.error.value"
      :empty="handoffsEmpty" @retry="handoffs.reload" />
    <HandoffList v-if="handoffs.data.value?.length" :items="handoffs.data.value" @resolve="resolveHandoff" />
  </section>
</template>

<style scoped>
.section { margin-bottom: 28px; }
.section-title { margin: 0 0 4px; font-size: 18px; }
.section-text { margin: 0 0 12px; color: var(--text-muted); }
.action-error { padding: 10px 14px; border-radius: var(--radius); background: var(--danger-soft); color: var(--danger); }
</style>
