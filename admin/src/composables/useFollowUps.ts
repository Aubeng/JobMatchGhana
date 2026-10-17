import { shallowRef } from 'vue'
import { api, ApiError } from '@/api/client'
import type { HandoffItem, InterestedItem } from '@/api/types'
import { useResource } from './useResource'

/** Staff work queues: connections workers asked for, and workers the bot couldn't help. */
export function useFollowUps() {
  const interested = useResource(() => api.get<InterestedItem[]>('/api/admin/followups/interested'))
  const handoffs = useResource(() => api.get<HandoffItem[]>('/api/admin/followups/handoffs'))
  const actionError = shallowRef<string | null>(null)

  async function act(call: () => Promise<unknown>, refresh: () => Promise<void>): Promise<void> {
    actionError.value = null
    try {
      await call()
      await refresh()
    } catch (e) {
      actionError.value = e instanceof ApiError ? e.message : 'That did not save. Please try again.'
    }
  }

  const setOutcome = (matchId: number, outcome: 'connected' | 'declined') =>
    act(() => api.post(`/api/admin/followups/interested/${matchId}/${outcome}`), interested.reload)

  const resolveHandoff = (id: number, note: string) =>
    act(() => api.post(`/api/admin/followups/handoffs/${id}/resolve`, { note: note || null }), handoffs.reload)

  return { interested, handoffs, actionError, setOutcome, resolveHandoff }
}
