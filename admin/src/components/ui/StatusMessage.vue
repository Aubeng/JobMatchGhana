<script setup lang="ts">
defineProps<{ loading?: boolean; error?: string | null; empty?: string | null }>()
defineEmits<{ retry: [] }>()
</script>

<template>
  <div v-if="loading" class="status" role="status">Loading…</div>
  <div v-else-if="error" class="status status-error" role="alert">
    {{ error }}
    <button type="button" class="btn btn-small" @click="$emit('retry')">Try again</button>
  </div>
  <div v-else-if="empty" class="status">{{ empty }}</div>
</template>

<style scoped>
.status { padding: 16px; color: var(--text-muted); }
.status-error { color: var(--danger); background: var(--danger-soft); border-radius: var(--radius); display: flex; gap: 12px; align-items: center; }
</style>
