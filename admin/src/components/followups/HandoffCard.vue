<script setup lang="ts">
import { shallowRef } from 'vue'
import type { HandoffItem } from '@/api/types'
import { dateTime, humanise, phone } from '@/utils/format'

defineProps<{ item: HandoffItem }>()
const emit = defineEmits<{ resolve: [id: number, note: string] }>()

const note = shallowRef('')
</script>

<template>
  <li class="handoff card">
    <div class="handoff-head">
      <p class="handoff-worker">
        <a :href="`https://wa.me/${item.workerPhone}`" target="_blank" rel="noopener">{{ phone(item.workerPhone) }}</a>
      </p>
      <span class="badge badge-warn">Stuck at: {{ humanise(item.state) }}</span>
    </div>
    <p class="handoff-time">Since {{ dateTime(item.createdUtc) }}</p>
    <p class="handoff-label">Their last messages</p>
    <ol class="handoff-messages">
      <li v-for="(message, i) in item.recentMessages" :key="i">{{ message || '(empty)' }}</li>
    </ol>
    <form class="handoff-resolve" @submit.prevent="emit('resolve', item.id, note)">
      <label class="field">
        <span class="field-label">What you did (optional)</span>
        <input v-model="note" class="input" maxlength="1000" placeholder="e.g. Called; finished the profile together." />
      </label>
      <button type="submit" class="btn btn-primary btn-small">Mark resolved</button>
    </form>
  </li>
</template>

<style scoped>
.handoff { display: grid; gap: 6px; }
.handoff > p { margin: 0; }
.handoff-head { display: flex; justify-content: space-between; gap: 8px; align-items: center; flex-wrap: wrap; }
.handoff-worker { margin: 0; font-weight: 700; }
.handoff-time, .handoff-label { color: var(--text-muted); font-size: 13px; }
.handoff-messages { margin: 0; padding-left: 20px; font-size: 14px; }
.handoff-resolve { display: flex; gap: 10px; align-items: flex-end; flex-wrap: wrap; margin-top: 6px; }
.handoff-resolve .field { flex: 1; min-width: 220px; }
</style>
