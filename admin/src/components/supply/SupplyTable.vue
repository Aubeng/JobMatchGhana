<script setup lang="ts">
import { computed } from 'vue'
import type { Supply } from '@/api/types'

const props = defineProps<{ cells: Supply['byTradeAndDistrict']; minimum: number }>()

// Trade rows x district columns.
const districts = computed(() => [...new Set(props.cells.map((c) => c.district))].sort())
const rows = computed(() => {
  const trades = [...new Set(props.cells.map((c) => c.trade))].sort()
  return trades.map((trade) => ({
    trade,
    counts: districts.value.map((district) => {
      const cell = props.cells.find((c) => c.trade === trade && c.district === district)
      return { district, label: !cell ? '' : cell.workers == null ? `<${props.minimum}` : String(cell.workers) }
    })
  }))
})
</script>

<template>
  <div class="card table-card">
    <div class="table-scroll">
      <table class="table">
        <caption class="visually-hidden">Confirmed workers by trade and district</caption>
        <thead>
          <tr>
            <th scope="col">Trade</th>
            <th v-for="d in districts" :key="d" scope="col" class="num">{{ d }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.trade">
            <th scope="row" class="trade">{{ row.trade }}</th>
            <td v-for="c in row.counts" :key="c.district" class="num">{{ c.label }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.table-card { padding: 0; overflow: hidden; }
.trade { background: transparent; color: var(--text); font-weight: 600; font-size: 14px; }
.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
</style>
