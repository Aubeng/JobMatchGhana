<script setup lang="ts">
import { computed } from 'vue'
import type { Supply } from '@/api/types'

const props = defineProps<{ skills: Supply['bySkill']; minimum: number }>()

const byTrade = computed(() => {
  const groups = new Map<string, { skill: string; label: string }[]>()
  for (const s of props.skills) {
    const list = groups.get(s.trade) ?? []
    list.push({ skill: s.skill, label: s.workers == null ? `<${props.minimum}` : String(s.workers) })
    groups.set(s.trade, list)
  }
  return [...groups.entries()].map(([trade, skills]) => ({ trade, skills }))
})
</script>

<template>
  <div class="groups">
    <section v-for="group in byTrade" :key="group.trade" class="group card">
      <h3 class="group-title">{{ group.trade }}</h3>
      <ul class="group-list">
        <li v-for="s in group.skills" :key="s.skill" class="group-item">
          <span>{{ s.skill }}</span><span class="group-count">{{ s.label }}</span>
        </li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; }
.group-title { margin: 0 0 8px; font-size: 15px; }
.group-list { list-style: none; margin: 0; padding: 0; display: grid; gap: 4px; font-size: 14px; }
.group-item { display: flex; justify-content: space-between; gap: 8px; }
.group-count { font-weight: 700; font-variant-numeric: tabular-nums; }
</style>
