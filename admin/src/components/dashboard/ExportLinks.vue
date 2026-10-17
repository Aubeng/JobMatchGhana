<template>
  <section class="exports card" aria-labelledby="exports-title">
    <h2 id="exports-title" class="exports-title">Research exports</h2>
    <p class="exports-text">
      Pseudonymised CSV for analysis: participants appear as stable ids, never phone numbers, and message text is
      left out (NFR-12).
    </p>
    <div class="exports-actions">
      <a class="btn" href="/api/admin/export/messages.csv" download>Messages CSV</a>
      <a class="btn" href="/api/admin/export/matches.csv" download>Matches CSV</a>
    </div>
  </section>
</template>

<style scoped>
.exports-title { margin: 0 0 6px; font-size: 17px; }
.exports-text { margin: 0 0 12px; color: var(--text-muted); font-size: 14px; }
.exports-actions { display: flex; gap: 8px; flex-wrap: wrap; }
</style>
