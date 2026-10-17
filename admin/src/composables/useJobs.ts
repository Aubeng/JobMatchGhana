import { shallowRef, watch } from 'vue'
import { api, ApiError } from '@/api/client'
import type { JobStatus, JobSummary } from '@/api/types'
import { useResource } from './useResource'

/** The job list with a status filter, plus publish/close actions that refresh it. */
export function useJobs() {
  const status = shallowRef<JobStatus | ''>('')
  const notice = shallowRef<string | null>(null)
  const actionError = shallowRef<string | null>(null)
  const busyId = shallowRef<number | null>(null)

  const list = useResource(
    () => api.get<JobSummary[]>(`/api/admin/jobs${status.value ? `?status=${status.value}` : ''}`),
    { immediate: false }
  )
  watch(status, () => list.reload(), { immediate: true })

  async function run(id: number, action: () => Promise<string>): Promise<void> {
    busyId.value = id
    notice.value = null
    actionError.value = null
    try {
      notice.value = await action()
      await list.reload()
    } catch (e) {
      actionError.value = e instanceof ApiError ? e.message : 'The action failed. Please try again.'
    } finally {
      busyId.value = null
    }
  }

  function publish(job: JobSummary): Promise<void> {
    return run(job.id, async () => {
      const { alerted, failed } = await api.post<{ alerted: number; failed: number }>(`/api/admin/jobs/${job.id}/publish`)
      const sent = `${alerted} nearby worker${alerted === 1 ? ' was' : 's were'} alerted`
      return `"${job.title}" is open. ${sent}${failed ? `; ${failed} alert${failed === 1 ? '' : 's'} could not be sent (see server log)` : ''}.`
    })
  }

  function close(job: JobSummary): Promise<void> {
    return run(job.id, async () => {
      await api.post(`/api/admin/jobs/${job.id}/close`)
      return `"${job.title}" is closed and no longer offered.`
    })
  }

  return { status, jobs: list.data, loading: list.loading, error: list.error, reload: list.reload, notice, actionError, busyId, publish, close }
}
