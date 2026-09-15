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

export type Page<T> = { total: number; offset: number; limit: number; items: T[] }

export type InstanceView = {
  instanceId: string
  instanceUrl: string
  version: string | null
  registeredAt: string
  lastSeenAt: string
  analyticsEnabled: boolean
  lastUsageReportAt: string | null
  scaleBucket: string | null
  pairedClients: number | null
  discordConnected: boolean | null
  termListsImported: string[] | null
  rateLimitColdStops: number | null
  wafBlocks: number | null
  ipAddress: string | null
}

export type InstanceIpView = { ipAddress: string; firstSeenAt: string; lastSeenAt: string; requests: number }

export type RegisterPageInstanceView = {
  instanceUrl: string
  firstSeenAt: string
  lastSeenAt: string
  visits: number
  alsoRegistered: boolean
}

export type RegistryStats = {
  registeredInstances: number
  activeLast30Days: number
  withAnalytics: number
  byVersion: Record<string, number>
  registerPageInstances: number
  registerPageOnly: number
}

const query = (params: Record<string, string | undefined>) => {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) if (value) search.set(key, value)
  const text = search.toString()
  return text ? `?${text}` : ''
}

export const api = {
  myInstances: () => request<{ items: ServerInstance[] }>('GET', '/api/my-instances'),
  localRegister: (url: string) => request<void>('POST', '/api/local-register', { url }),

  login: (key: string) => request<void>('POST', '/api/admin/login', { key }),
  logout: () => request<void>('POST', '/api/admin/logout'),
  session: () => request<{ signedIn: boolean }>('GET', '/api/admin/session'),

  stats: () => request<RegistryStats>('GET', '/api/stats'),
  instances: (search: string) =>
    request<Page<InstanceView>>('GET', `/api/instances${query({ search: search.trim() || undefined })}`),
  instance: (id: string) => request<InstanceView>('GET', `/api/instances/${encodeURIComponent(id)}`),
  ipHistory: (id: string) =>
    request<{ items: InstanceIpView[] }>('GET', `/api/instances/${encodeURIComponent(id)}/ip-history`),
  deleteInstance: (id: string) => request<void>('DELETE', `/api/instances/${encodeURIComponent(id)}`),
  registerPageInstances: (search: string) =>
    request<Page<RegisterPageInstanceView>>(
      'GET',
      `/api/register-page-instances${query({ search: search.trim() || undefined })}`,
    ),
  deleteRegisterPageInstance: (url: string) =>
    request<void>('DELETE', `/api/register-page-instances${query({ url })}`),
}
