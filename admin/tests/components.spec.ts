import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import JobForm from '@/components/jobs/JobForm.vue'
import MetricCard from '@/components/dashboard/MetricCard.vue'
import AnnotationCard from '@/components/annotation/AnnotationCard.vue'
import type { AnnotationTask, JobInput, Metric, Reference } from '@/api/types'
import { pay, percent, phone, humanise } from '@/utils/format'

const reference: Reference = {
  trades: [
    { id: 'construction', name: 'Construction & Building', skills: ['Block laying', 'Tiling'] },
    { id: 'hair_beauty', name: 'Hair & Beauty', skills: ['Braiding'] }
  ],
  areas: [{ label: 'Madina', district: 'La Nkwantanang Madina' }],
  payPeriods: ['day', 'week', 'month', 'job'],
  availabilities: ['Full-time', 'Part-time', 'Weekends only', 'Flexible'],
  intents: ['SKILL_DECLARE', 'EXIT', 'OTHER']
}

describe('format', () => {
  it('formats pay, percentages, phones and enum names', () => {
    expect(pay(180, 250, 'day')).toBe('GHS 180–250 per day')
    expect(pay(1500, 1500, 'month')).toBe('GHS 1,500 per month')
    expect(pay(null, null, null)).toBe('—')
    expect(percent(0.6)).toBe('60%')
    expect(percent(null)).toBe('—')
    expect(phone('233244123456')).toBe('0244 123 456')
    expect(humanise('ElicitTrade')).toBe('Elicit trade')
    expect(humanise('SKILL_DECLARE')).toBe('Skill declare')
  })
})

describe('JobForm', () => {
  const initial: JobInput = {
    title: 'Tiler', description: null, tradeId: 'construction', skills: ['Tiling'], area: 'Madina',
    payMin: 200, payMax: 280, payPeriod: 'day', availability: 'Full-time', employerName: null
  }

  it('submits a trimmed copy and never mutates the initial value', async () => {
    const wrapper = mount(JobForm, { props: { reference, initial, errors: {}, saving: false } })
    await wrapper.find('input.input').setValue('  Tiler for bathrooms  ')
    await wrapper.find('form').trigger('submit')
    const submitted = wrapper.emitted('submit')![0]![0] as JobInput
    expect(submitted.title).toBe('Tiler for bathrooms')
    expect(submitted.skills).toEqual(['Tiling'])
    expect(initial.title).toBe('Tiler')
  })

  it('drops skills from the old trade when the trade changes', async () => {
    const wrapper = mount(JobForm, { props: { reference, initial, errors: {}, saving: false } })
    await wrapper.find('select').setValue('hair_beauty')
    await wrapper.find('form').trigger('submit')
    expect((wrapper.emitted('submit')![0]![0] as JobInput).skills).toEqual([])
  })

  it('shows field errors from the API', () => {
    const wrapper = mount(JobForm, { props: { reference, initial, errors: { area: ['Unknown area.'] }, saving: false } })
    expect(wrapper.text()).toContain('Unknown area.')
  })
})

describe('MetricCard', () => {
  const metric = (value: number | null, meetsTarget: boolean | null): Metric =>
    ({ value, target: 0.85, higherIsBetter: true, n: 40, note: 'note', meetsTarget })

  it('shows value, target and status', () => {
    const ok = mount(MetricCard, { props: { label: 'Intent accuracy', metric: metric(0.9, true), unit: 'percent' } })
    expect(ok.text()).toContain('90%')
    expect(ok.text()).toContain('Target ≥ 85%')
    expect(ok.text()).toContain('Meets target')
    const none = mount(MetricCard, { props: { label: 'x', metric: metric(null, null), unit: 'percent' } })
    expect(none.text()).toContain('No data yet')
  })
})

describe('AnnotationCard', () => {
  const task = (id: number, skills: string[] = []): AnnotationTask => ({
    messageId: id, text: 'i do tiles', state: 'ElicitSkills', extractedSkills: skills,
    progress: { annotated: 0, eligible: 10, target: 2 }
  })

  it('needs an intent before saving and sends the skill judgement', async () => {
    const wrapper = mount(AnnotationCard, { props: { task: task(1, ['Tiling ("tiles")']), intents: reference.intents, saving: false } })
    const save = wrapper.findAll('button').find((b) => b.text().startsWith('Save'))!
    expect(save.attributes('disabled')).toBeDefined()
    await wrapper.find('input[value="SKILL_DECLARE"]').setValue(true)
    await wrapper.find('input[type="number"]').setValue('0')
    await save.trigger('click')
    expect(wrapper.emitted('submit')![0]).toEqual(['SKILL_DECLARE', 0])
  })

  it('is blind: the model prediction is never shown, and a new task resets the choice', async () => {
    const wrapper = mount(AnnotationCard, { props: { task: task(1), intents: reference.intents, saving: false } })
    await wrapper.find('input[value="EXIT"]').setValue(true)
    await wrapper.setProps({ task: task(2) })
    expect((wrapper.find('input[value="EXIT"]').element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.text()).toContain('None of these')
  })
})
