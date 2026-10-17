<script setup lang="ts">
import type { HandoffItem } from '@/api/types'
import HandoffCard from './HandoffCard.vue'

defineProps<{ items: HandoffItem[] }>()
const emit = defineEmits<{ resolve: [id: number, note: string] }>()
</script>

<template>
  <ul class="list">
    <HandoffCard v-for="item in items" :key="item.id" :item="item" @resolve="(id, note) => emit('resolve', id, note)" />
  </ul>
</template>

<style scoped>
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
</style>
