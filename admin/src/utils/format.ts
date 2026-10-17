// Pure display helpers (utilities, not composables).

export function percent(value: number | null | undefined, digits = 0): string {
  return value == null ? '—' : `${(value * 100).toFixed(digits)}%`
}

export function decimal(value: number | null | undefined, digits = 1): string {
  return value == null ? '—' : value.toFixed(digits)
}

export function dateTime(iso: string): string {
  return new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso))
}

const periods: Record<string, string> = { day: 'per day', week: 'per week', month: 'per month', job: 'for the job' }

export function pay(min: number | null, max: number | null, period: string | null): string {
  if (min == null && max == null) return '—'
  const fmt = (v: number) => v.toLocaleString('en-GB')
  const amount = min != null && max != null && min !== max ? `GHS ${fmt(min)}–${fmt(max)}` : `GHS ${fmt((min ?? max)!)}`
  return period ? `${amount} ${periods[period] ?? period}` : amount
}

/** "0244 123 456" style for WhatsApp ids like 233244123456. */
export function phone(waId: string): string {
  const local = waId.startsWith('233') ? `0${waId.slice(3)}` : waId
  return local.replace(/^(\d{4})(\d{3})(\d+)$/, '$1 $2 $3')
}

export function humanise(enumName: string): string {
  return enumName.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ').toLowerCase().replace(/^\w/, (c) => c.toUpperCase())
}
