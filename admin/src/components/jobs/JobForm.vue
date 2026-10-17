<script setup lang="ts">
import { computed, reactive, watch } from 'vue'
import type { JobInput, Reference } from '@/api/types'

const props = defineProps<{
  reference: Reference
  initial: JobInput | null
  errors: Record<string, string[]>
  saving: boolean
}>()
const emit = defineEmits<{ submit: [job: JobInput]; cancel: [] }>()

function blank(): JobInput {
  return {
    title: '', description: null, tradeId: '', skills: [], area: '', payMin: null, payMax: null,
    payPeriod: 'day', availability: 'Full-time', employerName: null
  }
}

// A local working copy: the parent's data only changes when the form is submitted.
const form = reactive<JobInput>(blank())
watch(
  () => props.initial,
  (initial) => Object.assign(form, blank(), initial ?? {}, { skills: [...(initial?.skills ?? [])] }),
  { immediate: true }
)

const trade = computed(() => props.reference.trades.find((t) => t.id === form.tradeId) ?? null)

// Skills belong to a trade: switching trade drops skills from the old one.
watch(
  () => form.tradeId,
  () => {
    const allowed = new Set(trade.value?.skills ?? [])
    form.skills = form.skills.filter((s) => allowed.has(s))
  }
)

function error(field: string): string | null {
  return props.errors[field]?.[0] ?? null
}

function toNumber(value: string): number | null {
  return value.trim() === '' ? null : Number(value)
}

function onSubmit(): void {
  emit('submit', {
    ...form,
    title: form.title.trim(),
    description: form.description?.trim() || null,
    employerName: form.employerName?.trim() || null,
    skills: [...form.skills]
  })
}
</script>

<template>
  <form class="job-form card" novalidate @submit.prevent="onSubmit">
    <label class="field field-wide">
      <span class="field-label">Job title</span>
      <input v-model="form.title" class="input" maxlength="200" :aria-invalid="!!error('title')" placeholder="e.g. Tiler for bathrooms and kitchens" />
      <span v-if="error('title')" class="field-error">{{ error('title') }}</span>
    </label>

    <label class="field field-wide">
      <span class="field-label">Description</span>
      <textarea v-model="form.description" class="textarea" maxlength="2000"
        placeholder="What the work involves, where, how long. Matching reads this text." />
      <span class="field-hint">Workers are matched on the title, this description and the skills below.</span>
      <span v-if="error('description')" class="field-error">{{ error('description') }}</span>
    </label>

    <label class="field">
      <span class="field-label">Trade</span>
      <select v-model="form.tradeId" class="select" :aria-invalid="!!error('tradeId')">
        <option value="" disabled>Choose a trade</option>
        <option v-for="t in reference.trades" :key="t.id" :value="t.id">{{ t.name }}</option>
      </select>
      <span v-if="error('tradeId')" class="field-error">{{ error('tradeId') }}</span>
    </label>

    <label class="field">
      <span class="field-label">Area</span>
      <input v-model="form.area" class="input" list="job-areas" :aria-invalid="!!error('area')" placeholder="e.g. Madina, Kaneshie, Tema" />
      <datalist id="job-areas">
        <option v-for="a in reference.areas" :key="a.label" :value="a.label">{{ a.district }}</option>
      </datalist>
      <span class="field-hint">A town, neighbourhood or district. It's resolved to a district for matching.</span>
      <span v-if="error('area')" class="field-error">{{ error('area') }}</span>
    </label>

    <fieldset class="field field-wide skills" :aria-invalid="!!error('skills')">
      <legend class="field-label">Skills needed</legend>
      <p v-if="!trade" class="field-hint">Choose a trade first.</p>
      <div v-else class="skills-grid">
        <label v-for="skill in trade.skills" :key="skill" class="skill">
          <input v-model="form.skills" type="checkbox" :value="skill" />
          {{ skill }}
        </label>
      </div>
      <span v-if="error('skills')" class="field-error">{{ error('skills') }}</span>
    </fieldset>

    <div class="field pay">
      <span class="field-label">Pay (GHS)</span>
      <div class="pay-row">
        <input class="input" type="number" min="0" inputmode="numeric" aria-label="Minimum pay"
          :value="form.payMin ?? ''" placeholder="Min" @input="form.payMin = toNumber(($event.target as HTMLInputElement).value)" />
        <span aria-hidden="true">–</span>
        <input class="input" type="number" min="0" inputmode="numeric" aria-label="Maximum pay" :aria-invalid="!!error('payMax')"
          :value="form.payMax ?? ''" placeholder="Max" @input="form.payMax = toNumber(($event.target as HTMLInputElement).value)" />
        <select v-model="form.payPeriod" class="select" aria-label="Pay period">
          <option v-for="p in reference.payPeriods" :key="p" :value="p">per {{ p }}</option>
        </select>
      </div>
      <span v-if="error('payMin') || error('payMax') || error('payPeriod')" class="field-error">
        {{ error('payMin') ?? error('payMax') ?? error('payPeriod') }}
      </span>
    </div>

    <label class="field">
      <span class="field-label">When</span>
      <select v-model="form.availability" class="select">
        <option v-for="a in reference.availabilities" :key="a" :value="a">{{ a }}</option>
      </select>
      <span class="field-hint">Only workers with compatible availability are matched.</span>
    </label>

    <label class="field">
      <span class="field-label">Employer (shown to workers)</span>
      <input v-model="form.employerName" class="input" maxlength="120" placeholder="e.g. Building contractor" />
      <span class="field-hint">No phone numbers: the JobMatch team connects interested workers.</span>
    </label>

    <div class="actions field-wide">
      <button type="button" class="btn" @click="emit('cancel')">Cancel</button>
      <button type="submit" class="btn btn-primary" :disabled="saving">{{ saving ? 'Saving…' : 'Save' }}</button>
    </div>
  </form>
</template>

<style scoped>
.job-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px 20px; }
.field-wide { grid-column: 1 / -1; }
.skills { border: 0; margin: 0; padding: 0; min-width: 0; }
.skills-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 6px 14px; }
.skill { display: flex; gap: 8px; align-items: center; font-size: 14px; }
.pay-row { display: grid; grid-template-columns: 1fr auto 1fr 1.2fr; gap: 8px; align-items: center; }
.actions { display: flex; justify-content: flex-end; gap: 8px; }
@media (max-width: 720px) { .job-form { grid-template-columns: 1fr; } }
</style>
