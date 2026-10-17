import { shallowRef } from 'vue'
import { api, ApiError } from '@/api/client'
import type { AnnotationSummary, AnnotationTask } from '@/api/types'
import { useResource } from './useResource'

/** One blind coding task at a time, plus the running accuracy/precision summary. */
export function useAnnotation() {
  const task = useResource(() => api.get<AnnotationTask | undefined>('/api/admin/annotation/next'))
  const summary = useResource(() => api.get<AnnotationSummary>('/api/admin/annotation/summary'))
  const saving = shallowRef(false)
  const saveError = shallowRef<string | null>(null)

  async function submit(intent: string, skillsCorrect: number | null): Promise<void> {
    const current = task.data.value
    if (!current) return
    saving.value = true
    saveError.value = null
    try {
      await api.post(`/api/admin/annotation/${current.messageId}`, { intent, skillsCorrect })
      await Promise.all([task.reload(), summary.reload()])
    } catch (e) {
      saveError.value = e instanceof ApiError ? e.message : 'Saving failed. Please try again.'
    } finally {
      saving.value = false
    }
  }

  return { task, summary, saving, saveError, submit, skip: task.reload }
}
