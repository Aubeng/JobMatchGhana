// Thin fetch wrapper for the same-origin admin API.
// - Sends the session cookie (same origin) and the CSRF header the API requires on writes.
// - Turns problem responses into ApiError with field errors for forms.
// - Reports 401s to one handler (the router sends the user to sign in).

export const CSRF_HEADER = 'X-JobMatch-Admin'

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly fieldErrors: Record<string, string[]> = {}
  ) {
    super(message)
  }
}

let onUnauthorized: () => void = () => {}

export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: {
      [CSRF_HEADER]: '1',
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' })
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  })

  if (response.status === 401 && !path.startsWith('/api/auth/')) onUnauthorized()
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}))
    throw new ApiError(
      response.status,
      problem.detail ?? problem.title ?? `Request failed (${response.status})`,
      problem.errors ?? {}
    )
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T>(path: string, body: unknown) => request<T>('PUT', path, body)
}
