import type { SeenServer } from './merge.ts'
import { seenServersFrom } from './storage.ts'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

/**
 * Longer than my.modbot.co's own wait on Modbot Cloud (ten seconds), so a my.modbot.co that is up
 * and waiting on Cloud gets to answer, and one that is not answering at all is given up on.
 */
const REQUEST_TIMEOUT_MS = 15_000

/** True for an answer that says try again later, rather than one that says this will never work. */
export function isWorthRetrying(error: unknown): boolean {
  if (!(error instanceof ApiError)) return true
  return error.status >= 500 || error.status === 408 || error.status === 429
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
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
 * key that never reaches the browser (central services spec 2.1.1).
 */
export const api = {
  myServers: async (): Promise<{ items: SeenServer[] }> => {
    const answer = await request<{ items?: unknown } | null>('GET', '/api/my-servers')
    // A list that is not one is a failed answer, not an empty list: the page keeps the one it had.
    if (!answer || !Array.isArray(answer.items)) throw new ApiError(502, 'Not a list of servers.')
    return { items: seenServersFrom(answer.items) }
  },
  localRegister: (url: string) => request<void>('POST', '/api/local-register', { url }),
}
