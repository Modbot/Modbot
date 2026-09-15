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

export type InstallView = {
  installId: string
  clientVersion: string
  firstSeenAt: string
  lastSeenAt: string
  linesStored: number
  clockOffsetMs: number | null
  clockDisagrees: boolean
  modbotServerId: string | null
}

export type InstallPage = { total: number; offset: number; limit: number; items: InstallView[] }

/** One stored line. `text` is plain text with any instance nonce already hidden by the server. */
export type LineView = {
  receivedAt: string
  sentAt: string
  loggedAt: string | null
  utcOffsetMinutes: number | null
  file: string
  offset: number
  text: string
}

export type DayCount = { day: string; lines: number }

export type Settings = { logLineKeepDays: number; logEventKeepDays: number }

export const api = {
  login: (key: string) => request<void>('POST', '/api/admin/login', { key }),
  logout: () => request<void>('POST', '/api/admin/logout'),
  session: () => request<{ signedIn: boolean }>('GET', '/api/admin/session'),

  installs: (offset: number, limit: number) =>
    request<InstallPage>('GET', `/api/admin/installs?offset=${offset}&limit=${limit}`),
  install: (id: string) => request<InstallView>('GET', `/api/admin/installs/${encodeURIComponent(id)}`),
  lines: (id: string, limit: number) =>
    request<{ items: LineView[] }>('GET', `/api/admin/installs/${encodeURIComponent(id)}/lines?limit=${limit}`),
  linesPerDay: (days: number) => request<{ items: DayCount[] }>('GET', `/api/admin/lines-per-day?days=${days}`),
  settings: () => request<Settings>('GET', '/api/admin/settings'),
  saveSettings: (settings: Settings) => request<Settings>('PUT', '/api/admin/settings', settings),
}
