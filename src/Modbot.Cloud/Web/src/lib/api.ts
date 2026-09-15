export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    // The admin session is an HttpOnly cookie; the key itself never passes through here twice.
    credentials: 'same-origin',
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

export const api = {
  login: (key: string) => request<void>('POST', '/api/admin/login', { key }),
  logout: () => request<void>('POST', '/api/admin/logout'),
  session: () => request<{ signedIn: boolean }>('GET', '/api/admin/session'),
}
