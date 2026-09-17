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
  companionVersion: string
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
  companionEventId: string
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

export type Settings = { eventKeepDays: number; logKeepDays: number }

export type ShowcaseKind = 'sponsor' | 'early-adopter'

export type ShowcaseEntry = {
  id: string
  kind: ShowcaseKind
  name: string
  link: string
  imageUrl: string
  vrChatGroupId: string | null
  groupImageUrl: string | null
  groupBannerUrl: string | null
  sortOrder: number
  addedAt: string
}

export type ShowcaseUpdate = {
  kind: ShowcaseKind
  name: string
  link: string
  imageUrl: string
  vrChatGroupId: string | null
  groupImageUrl: string | null
  groupBannerUrl: string | null
  sortOrder: number
}

export type InstanceAlertView = {
  on: boolean
  email: string
  silentAfterMinutes: number
  errorsAnHour: number
  quietHours: number
  problem: boolean
  since: string | null
  detail: string | null
  lastSentAt: string | null
  lastError: string | null
  mailConfigured: boolean
  /** The address the next email would go to: this row's, or the owning account's. */
  sendsTo: string | null
}

export type InstanceAlertUpdate = {
  on: boolean
  email: string
  silentAfterMinutes: number
  errorsAnHour: number
  quietHours: number
}

export type LogLevel = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

/** One log line a Modbot deployment sent. Every field is somebody else's text. */
export type LogLineView = {
  id: number
  serverId: string
  receivedAt: string
  at: string
  level: LogLevel
  message: string
  template: string | null
  source: string | null
  area: string | null
  service: string | null
  version: string | null
  exception: string | null
  properties: string
}

export type LogLinePage = { items: LogLineView[]; next: number | null }

export type LogSenderView = {
  serverId: string
  groupName: string | null
  version: string | null
  lastSeenAt: string
}

export type LogQuery = {
  serverId?: string
  level?: LogLevel
  source?: string
  text?: string
  from?: string
  to?: string
  before?: number
  limit?: number
}

export type AccountView = {
  email: string
  emailVerified: boolean
  pendingEmail: string | null
  createdAt: string
}

/** A Modbot server this account has claimed. */
export type ServerView = {
  serverId: string
  publicAddress: string | null
  version: string | null
  hostPlatform: string | null
  groupId: string | null
  groupName: string | null
  groupDescription: string | null
  groupIconUrl: string | null
  groupBannerUrl: string | null
  discordConnected: boolean | null
  termListsImported: string[] | null
  rateLimitColdStops: number | null
  wafBlocks: number | null
  aiModerationEnabled: boolean | null
  registeredAt: string
  lastReportAt: string | null
  lastSeenAt: string
  claimedAt: string | null
}

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
  logs: (query: LogQuery = {}) => {
    const q = new URLSearchParams()
    if (query.serverId) q.set('serverId', query.serverId)
    if (query.level) q.set('level', query.level)
    if (query.source) q.set('source', query.source)
    if (query.text) q.set('text', query.text)
    if (query.from) q.set('from', query.from)
    if (query.to) q.set('to', query.to)
    if (query.before) q.set('before', String(query.before))
    if (query.limit) q.set('limit', String(query.limit))
    const search = q.toString()
    return request<LogLinePage>('GET', `/api/admin/logs${search ? `?${search}` : ''}`)
  },
  logSenders: () => request<{ items: LogSenderView[] }>('GET', '/api/admin/logs/senders'),
  showcase: () => request<{ items: ShowcaseEntry[] }>('GET', '/api/admin/showcase'),
  addShowcase: (body: ShowcaseUpdate) => request<ShowcaseEntry>('POST', '/api/admin/showcase', body),
  saveShowcase: (id: string, body: ShowcaseUpdate) =>
    request<ShowcaseEntry>('PUT', `/api/admin/showcase/${encodeURIComponent(id)}`, body),
  removeShowcase: (id: string) =>
    request<void>('DELETE', `/api/admin/showcase/${encodeURIComponent(id)}`),

  instanceAlerts: (id: string) =>
    request<InstanceAlertView>('GET', `/api/admin/servers/${encodeURIComponent(id)}/alerts`),
  saveInstanceAlerts: (id: string, body: InstanceAlertUpdate) =>
    request<InstanceAlertView>('PUT', `/api/admin/servers/${encodeURIComponent(id)}/alerts`, body),
  settings: () => request<Settings>('GET', '/api/admin/settings'),
  saveSettings: (settings: Settings) => request<Settings>('PUT', '/api/admin/settings', settings),

  registerAccount: (email: string, password: string) =>
    request<void>('POST', '/api/v1/accounts', { email, password }),
  verifyEmail: (token: string) => request<void>('POST', '/api/v1/accounts/verify', { token }),
  verifyEmailChange: (token: string) => request<void>('POST', '/api/v1/accounts/verify-email-change', { token }),
  signIn: (email: string, password: string) =>
    request<void>('POST', '/api/v1/accounts/session', { email, password }),
  signOut: () => request<void>('DELETE', '/api/v1/accounts/session'),
  me: () => request<AccountView>('GET', '/api/v1/accounts/me'),
  forgotPassword: (email: string) => request<void>('POST', '/api/v1/accounts/forgot-password', { email }),
  resetPassword: (token: string, password: string) =>
    request<void>('POST', '/api/v1/accounts/reset-password', { token, password }),
  changePassword: (currentPassword: string, password: string) =>
    request<void>('POST', '/api/v1/accounts/password', { currentPassword, password }),
  changeEmail: (email: string, password: string) =>
    request<void>('POST', '/api/v1/accounts/email', { email, password }),

  myServers: () => request<{ items: ServerView[] }>('GET', '/api/v1/servers/mine'),
  claimServer: (code: string) => request<ServerView>('POST', '/api/v1/servers/claim', { code }),
  unclaimServer: (serverId: string) =>
    request<void>('DELETE', `/api/v1/servers/${encodeURIComponent(serverId)}/claim`),
}
