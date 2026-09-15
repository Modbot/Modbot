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
  eventsStored: number
  clockOffsetMs: number | null
  clockDisagrees: boolean
  modbotServerId: string | null
}

export type InstallPage = { total: number; offset: number; limit: number; items: InstallView[] }

/** One stored event. Every field is plain text; nothing here is a link. */
export type EventView = {
  clientEventId: string
  receivedAt: string
  sentAt: string
  occurredAt: string
  type: string
  typeRaw: string | null
  subjectId: string
  displayName: string | null
  worldId: string
  instanceId: string
  groupId: string | null
  data: string
}

export type DayCount = { day: string; events: number }

export type Settings = { eventKeepDays: number }

export const api = {
  login: (key: string) => request<void>('POST', '/api/admin/login', { key }),
  logout: () => request<void>('POST', '/api/admin/logout'),
  session: () => request<{ signedIn: boolean }>('GET', '/api/admin/session'),

  installs: (offset: number, limit: number) =>
    request<InstallPage>('GET', `/api/admin/installs?offset=${offset}&limit=${limit}`),
  install: (id: string) => request<InstallView>('GET', `/api/admin/installs/${encodeURIComponent(id)}`),
  events: (id: string, limit: number) =>
    request<{ items: EventView[] }>('GET', `/api/admin/installs/${encodeURIComponent(id)}/events?limit=${limit}`),
  eventsPerDay: (days: number) => request<{ items: DayCount[] }>('GET', `/api/admin/events-per-day?days=${days}`),
  settings: () => request<Settings>('GET', '/api/admin/settings'),
  saveSettings: (settings: Settings) => request<Settings>('PUT', '/api/admin/settings', settings),
}
