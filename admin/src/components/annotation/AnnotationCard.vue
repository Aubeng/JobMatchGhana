<script setup lang="ts">
import { computed, shallowRef, watch } from 'vue'
import type { AnnotationTask } from '@/api/types'
import { humanise } from '@/utils/format'

const props = defineProps<{ task: AnnotationTask; intents: string[]; saving: boolean }>()
const emit = defineEmits<{ submit: [intent: string, skillsCorrect: number | null]; skip: [] }>()

const intent = shallowRef<string | null>(null)
const skillsCorrect = shallowRef(0)

// A new message starts a fresh judgement.
watch(
  () => props.task.messageId,
  () => {
    intent.value = null
    skillsCorrect.value = props.task.extractedSkills.length
  },
  { immediate: true }
)

const hasSkills = computed(() => props.task.extractedSkills.length > 0)
const canSave = computed(() => intent.value !== null && !props.saving)

function save(): void {
  if (intent.value) emit('submit', intent.value, hasSkills.value ? skillsCorrect.value : null)
}
</script>

<template>
  <article class="task card" aria-labelledby="task-text">
    <p class="task-context">Worker message (sent while the bot was asking: {{ humanise(task.state) }})</p>
    <blockquote id="task-text" class="task-text">{{ task.text }}</blockquote>

    <fieldset class="task-group">
      <legend class="field-label">1. What did the worker mean?</legend>
      <p class="field-hint">Judge the message yourself; the model's answer is hidden so it can't sway you.</p>
      <div class="intents">
        <label v-for="option in intents" :key="option" class="intent" :class="{ 'intent-selected': intent === option }">
          <input v-model="intent" type="radio" name="intent" :value="option" class="visually-hidden" />
          {{ option === 'OTHER' ? 'None of these' : humanise(option) }}
        </label>
      </div>
    </fieldset>

    <fieldset v-if="hasSkills" class="task-group">
      <legend class="field-label">2. How many of these extracted skills are right?</legend>
      <ul class="skills">
        <li v-for="skill in task.extractedSkills" :key="skill">{{ skill }}</li>
      </ul>
      <label class="skills-count">
        Correct:
        <input v-model.number="skillsCorrect" class="input" type="number" min="0" :max="task.extractedSkills.length" />
        of {{ task.extractedSkills.length }}
      </label>
    </fieldset>

    <div class="task-actions">
      <button type="button" class="btn" :disabled="saving" @click="emit('skip')">Skip</button>
      <button type="button" class="btn btn-primary" :disabled="!canSave" @click="save">
        {{ saving ? 'Saving…' : 'Save and next' }}
      </button>
    </div>
  </article>
</template>

<style scoped>
.task { display: grid; gap: 16px; }
.task-context { margin: 0; color: var(--text-muted); font-size: 13px; }
.task-text { margin: 0; padding: 14px 16px; border-left: 4px solid var(--accent); background: var(--surface-muted); border-radius: 0 8px 8px 0; font-size: 18px; }
.task-group { border: 0; margin: 0; padding: 0; display: grid; gap: 8px; }
.intents { display: flex; flex-wrap: wrap; gap: 8px; }
.intent { padding: 7px 12px; border: 1px solid var(--border); border-radius: 999px; cursor: pointer; background: var(--surface); font-size: 14px; }
.intent:hover { border-color: var(--accent); }
.intent:focus-within { outline: 3px solid color-mix(in srgb, var(--accent) 45%, transparent); }
.intent-selected { background: var(--accent); border-color: var(--accent); color: #fff; font-weight: 600; }
.skills { margin: 0; padding-left: 20px; }
.skills-count { display: flex; gap: 8px; align-items: center; }
.skills-count .input { width: 80px; }
.task-actions { display: flex; justify-content: flex-end; gap: 8px; }
</style>
