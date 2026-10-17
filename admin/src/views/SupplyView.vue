<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import StatusMessage from '@/components/ui/StatusMessage.vue'
import SupplyTable from '@/components/supply/SupplyTable.vue'
import SkillSupplyList from '@/components/supply/SkillSupplyList.vue'
import { useSupply } from '@/composables/useSupply'

const { data, loading, error, reload } = useSupply()

const subtitle = computed(() => {
  const min = data.value?.minimumCount ?? 3
  return `Workers with a confirmed profile, by trade and area (FR-17). Counts below ${min} show as "<${min}" so no one can be identified.`
})
const total = computed(() => {
  if (!data.value) return ''
  const t = data.value.totalWorkers
  return t == null ? `Fewer than ${data.value.minimumCount} workers so far` : `${t} workers`
})
const empty = computed(() => (data.value && data.value.byTradeAndDistrict.length === 0 ? 'No confirmed profiles yet.' : null))
</script>

<template>
  <PageHeader title="Skills supply" :subtitle="subtitle">
    <template #actions><span class="total">{{ total }}</span></template>
  </PageHeader>
  <StatusMessage :loading="loading && !data" :error="error" :empty="empty" @retry="reload" />
  <template v-if="data && data.byTradeAndDistrict.length">
    <SupplyTable :cells="data.byTradeAndDistrict" :minimum="data.minimumCount" />
    <h2 class="section-title">By skill</h2>
    <SkillSupplyList :skills="data.bySkill" :minimum="data.minimumCount" />
  </template>
</template>

<style scoped>
.total { align-self: center; font-weight: 700; }
.section-title { font-size: 18px; margin: 24px 0 10px; }
</style>
