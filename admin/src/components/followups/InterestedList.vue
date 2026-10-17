<script setup lang="ts">
import type { InterestedItem } from '@/api/types'
import { dateTime, phone } from '@/utils/format'

defineProps<{ items: InterestedItem[] }>()
const emit = defineEmits<{ outcome: [matchId: number, outcome: 'connected' | 'declined'] }>()
</script>

<template>
  <ul class="list">
    <li v-for="item in items" :key="item.matchId" class="item card">
      <div class="item-main">
        <p class="item-title">
          {{ item.jobTitle }}
          <span v-if="item.jobIsSynthetic" class="badge">synthetic job</span>
        </p>
        <p class="item-meta">{{ item.jobArea }} · {{ item.employer ?? 'Employer not named' }}</p>
        <p class="item-worker">
          Worker <a :href="`https://wa.me/${item.workerPhone}`" target="_blank" rel="noopener">{{ phone(item.workerPhone) }}</a>
          · {{ item.workerTrade ?? '—' }} · {{ item.workerArea ?? '—' }}
        </p>
        <p class="item-time">Interested {{ dateTime(item.interestedUtc) }}</p>
      </div>
      <div class="item-actions">
        <button type="button" class="btn btn-small btn-primary" @click="emit('outcome', item.matchId, 'connected')">Connected</button>
        <button type="button" class="btn btn-small" @click="emit('outcome', item.matchId, 'declined')">Not taken up</button>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
.item { display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
.item-main > p { margin: 0; }
.item-title { font-weight: 700; display: flex; gap: 8px; align-items: center; }
.item-meta, .item-time { color: var(--text-muted); font-size: 13px; }
.item-worker { margin-top: 6px !important; }
.item-actions { display: flex; gap: 8px; align-items: center; }
</style>
