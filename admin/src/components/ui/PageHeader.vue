<script setup lang="ts">
defineProps<{ title: string; subtitle?: string }>()
</script>

<template>
  <div class="page-header">
    <div>
      <h1 class="page-title">{{ title }}</h1>
      <p v-if="subtitle" class="page-subtitle">{{ subtitle }}</p>
    </div>
    <div class="page-actions"><slot name="actions" /></div>
  </div>
</template>

<style scoped>
.page-header { display: flex; justify-content: space-between; align-items: flex-end; gap: 16px; flex-wrap: wrap; margin-bottom: 20px; }
.page-title { margin: 0; font-size: 24px; }
.page-subtitle { margin: 4px 0 0; color: var(--text-muted); max-width: 70ch; }
.page-actions { display: flex; gap: 8px; flex-wrap: wrap; }
</style>
