import { shallowRef, type Ref, type ShallowRef } from 'vue'
import { ApiError } from '@/api/client'

export interface Resource<T> {
  data: ShallowRef<T | null>
  loading: Ref<boolean>
  error: Ref<string | null>
  reload: () => Promise<void>
}

/** Load something once (and on demand): data, loading, error, reload. */
export function useResource<T>(loader: () => Promise<T>, options: { immediate?: boolean } = {}): Resource<T> {
  const data = shallowRef<T | null>(null)
  const loading = shallowRef(false)
  const error = shallowRef<string | null>(null)

  async function reload(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      data.value = await loader()
    } catch (e) {
      error.value = e instanceof ApiError ? e.message : 'Something went wrong. Please try again.'
    } finally {
      loading.value = false
    }
  }

  if (options.immediate !== false) void reload()
  return { data, loading, error, reload }
}
