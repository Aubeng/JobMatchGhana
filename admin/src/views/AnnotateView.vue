<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import AnnotationCard from '@/components/annotation/AnnotationCard.vue'
import AnnotationSummaryPanel from '@/components/annotation/AnnotationSummaryPanel.vue'
import { useAnnotation } from '@/composables/useAnnotation'
import { useReference } from '@/composables/useReference'

const { task, summary, saving, saveError, submit, skip } = useAnnotation()
const reference = useReference()

const done = computed(() =>
  !task.loading.value && !task.error.value && !task.data.value ? 'No messages left to code. Well done.' : null
)
</script>

<template>
  <PageHeader
    title="Annotate"
    subtitle="Blind manual coding of real pilot messages: the method the Specification sets for intent accuracy and entity precision (§4.2)."
  />
  <div class="layout">
    <div>
      <StatusMessage :loading="task.loading.value && !task.data.value" :error="task.error.value" :empty="done" @retry="task.reload" />
      <p v-if="saveError" class="save-error" role="alert">{{ saveError }}</p>
      <AnnotationCard
        v-if="task.data.value && reference.data.value"
        :task="task.data.value"
        :intents="reference.data.value.intents"
        :saving="saving"
        @submit="submit"
        @skip="skip"
      />
    </div>
    <AnnotationSummaryPanel v-if="summary.data.value" :summary="summary.data.value" />
  </div>
</template>

<style scoped>
.layout { display: grid; grid-template-columns: minmax(0, 3fr) minmax(280px, 2fr); gap: 18px; align-items: start; }
.save-error { padding: 10px 14px; border-radius: var(--radius); background: var(--danger-soft); color: var(--danger); }
@media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
</style>
