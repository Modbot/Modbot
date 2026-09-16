import type { ServerInstance } from './merge.ts'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (!response.ok) {
    let message = `Request failed (${response.status})`
    try {
      const data: unknown = await response.json()
      if (data && typeof data === 'object' && typeof (data as { error?: unknown }).error === 'string')
        message = (data as { error: string }).error
    } catch {
      // Not JSON.
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204 || response.status === 202) return undefined as T
  return (await response.json()) as T
}

/**
 * The two calls the page makes. Both go to my.modbot.co, which passes them to Modbot Cloud with a
 * key that stays on the server (central services spec 2.1.1).
 */
export const api = {
  myInstances: () => request<{ items: ServerInstance[] }>('GET', '/api/my-instances'),
  localRegister: (url: string) => request<void>('POST', '/api/local-register', { url }),
}
