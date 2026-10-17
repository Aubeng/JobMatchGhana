import { api } from '@/api/client'
import type { Supply } from '@/api/types'
import { useResource } from './useResource'

export function useSupply() {
  return useResource(() => api.get<Supply>('/api/admin/supply'))
}
