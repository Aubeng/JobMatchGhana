import { api } from '@/api/client'
import type { Analytics } from '@/api/types'
import { useResource } from './useResource'

export function useAnalytics() {
  return useResource(() => api.get<Analytics>('/api/admin/analytics'))
}
