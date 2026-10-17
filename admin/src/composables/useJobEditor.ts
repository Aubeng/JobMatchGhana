import { shallowRef, type Ref } from 'vue'
import { api, ApiError } from '@/api/client'
import type { JobDetail, JobInput } from '@/api/types'
import { useResource } from './useResource'

/** Load (when editing) and save one job. Field errors come back from the API's validation. */
export function useJobEditor(id: Ref<number | null>) {
  const job = useResource(
    async () => (id.value == null ? null : await api.get<JobDetail>(`/api/admin/jobs/${id.value}`)),
    { immediate: id.value != null }
  )
  const saving = shallowRef(false)
  const fieldErrors = shallowRef<Record<string, string[]>>({})
  const saveError = shallowRef<string | null>(null)

  /** Returns the saved job, or null when validation failed. */
  async function save(input: JobInput): Promise<JobDetail | null> {
    saving.value = true
    fieldErrors.value = {}
    saveError.value = null
    try {
      return id.value == null
        ? await api.post<JobDetail>('/api/admin/jobs', input)
        : await api.put<JobDetail>(`/api/admin/jobs/${id.value}`, input)
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length > 0) fieldErrors.value = e.fieldErrors
      else saveError.value = e instanceof ApiError ? e.message : 'Saving failed. Please try again.'
      return null
    } finally {
      saving.value = false
    }
  }

  return { job: job.data, loading: job.loading, loadError: job.error, saving, fieldErrors, saveError, save }
}
