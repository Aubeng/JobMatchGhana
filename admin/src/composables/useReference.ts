import { api } from '@/api/client'
import type { Reference } from '@/api/types'
import { useResource } from './useResource'

// Reference data (taxonomy, areas, options) rarely changes: fetch once per page load.
let cached: Promise<Reference> | null = null

function load(): Promise<Reference> {
  cached ??= api.get<Reference>('/api/admin/reference').catch((e) => {
    cached = null
    throw e
  })
  return cached
}

export function useReference() {
  return useResource(load)
}
