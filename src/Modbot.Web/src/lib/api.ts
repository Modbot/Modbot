/**
 * The typed edge of Modbot's HTTP API.
 *
 * Everything the SPA knows about the server is in this file. Handlers elsewhere get values and
 * errors, never a Response -- which is what keeps "did you remember to check response.ok?" from
 * being a question that has to be answered correctly in thirty places.
 */

/** Where the wizard should resume. Mirrors the server's OnboardingStep. */
export type OnboardingStep =
  | 'Administrator'
  | 'VRChat'
  | 'Connection'
  | 'Group'
  | 'Optional'
  | 'Done'

/**
 * Why a connection attempt failed, at the granularity the remedy differs on.
 *
 * `WafBlocked` is the only one a proxy fixes. The server says so explicitly in `proxyWouldHelp`
 * rather than leaving the browser to re-derive it, so there is exactly one place that decision
 * can be got wrong.
 */
export type ConnectionOutcome =
  | 'Ok'
  | 'NotConfigured'
  | 'DnsFailure'
  | 'Timeout'
  | 'NetworkFailure'
  | 'WafBlocked'
  | 'RateLimited'
  | 'CredentialsRejected'
  | 'TwoFactorMissing'
  | 'Error'

export type ConnectionDiagnosis = {
  outcome: ConnectionOutcome
  proxyWouldHelp: boolean
  headline: string
  detail: string
  nextStep: string | null
  displayName: string | null
  statusCode: number
  wafCode: number | null
  elapsedMs: number
}

export type OnboardingStatus = {
  hasAdministrator: boolean
  authenticated: boolean
  onboardingComplete: boolean
  nextStep: OnboardingStep
  vrChat: { username: string | null; displayName: string | null; verifiedAt: string | null }
  connection: {
    checkedAt: string | null
    proxyUrl: string | null
    proxyUsername: string | null
    proxyPasswordStored: boolean
  }
  group: { id: string; name: string } | null
  integrations: {
    discordConfigured: boolean
    discordGuildId: string | null
    smtpConfigured: boolean
    smtpHost: string | null
  }
}

export type GroupCandidate = {
  id: string
  name: string
  memberCount: number
  iconUrl: string | null
  shortCode: string | null
  permissions: string[]
  missingPermissions: string[]
}

export type GroupCandidates = {
  groups: GroupCandidate[]
  totalGroups: number
  requiredPermissions: string[]
}

export type CurrentUser = { id: string; username: string; permissions: number }

export type StorageHorizon = {
  months: number
  projectedBytes: number
  monthlyCost: number | null
}

/**
 * `confidence` is Insufficient / Low / Good. Insufficient means the server declined to
 * extrapolate and `horizons` is empty — a number on a screen gets believed regardless of the
 * caveat beside it, so the honest output from a few hours of history is no number.
 */
export type DataSettings = {
  retention: { moderationFactRetentionDays: number; presenceFactRetentionDays: number }
  storage: {
    bytes: number
    facts: number
    bytesPerFact: number
    factsPerDay: number
    observedDays: number
    confidence: 'Insufficient' | 'Low' | 'Good'
    horizons: StorageHorizon[]
    capacityExhausted: string | null
  }
  deployment: {
    version: string
    platform: string
    platformEvidence: string | null
    logFilesWritten: boolean
    persistenceExplanation: string
  }
}

/**
 * A non-2xx response, carrying whatever the server said about it.
 *
 * `diagnosis` is populated when the body is a ConnectionDiagnosis -- the VRChat and group steps
 * answer 422 with one, and the difference between "a proxy fixes this" and "a proxy cannot
 * possibly fix this" is the whole point of the connection step, so it must not be flattened into
 * a string on the way through.
 */
export class ApiError extends Error {
  readonly status: number
  readonly diagnosis: ConnectionDiagnosis | null

  constructor(status: number, message: string, diagnosis: ConnectionDiagnosis | null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.diagnosis = diagnosis
  }
}

function isDiagnosis(body: unknown): body is ConnectionDiagnosis {
  return (
    typeof body === 'object' &&
    body !== null &&
    'outcome' in body &&
    'proxyWouldHelp' in body
  )
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response

  try {
    response = await fetch(path, {
      ...init,
      headers: init?.body ? { 'content-type': 'application/json', ...init?.headers } : init?.headers,
    })
  } catch {
    // fetch only rejects when the request never completed. Saying "the server is unreachable" is
    // honest here and avoids reporting a dropped connection as though the server had refused.
    throw new ApiError(0, 'Could not reach the Modbot server. Is it still running?', null)
  }

  if (response.status === 204) return undefined as T

  const text = await response.text()
  const body: unknown = text ? JSON.parse(text) : null

  if (response.ok) return body as T

  if (isDiagnosis(body)) throw new ApiError(response.status, body.headline, body)

  const message =
    typeof body === 'object' && body !== null && 'error' in body
      ? String((body as { error: unknown }).error)
      : `The server answered ${response.status}.`

  throw new ApiError(response.status, message, null)
}

const post = <T>(path: string, body?: unknown): Promise<T> =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

export const api = {
  onboardingStatus: () => request<OnboardingStatus>('/api/onboarding/status'),

  createAdministrator: (body: { username: string; password: string; confirmPassword: string }) =>
    post<CurrentUser>('/api/onboarding/administrator', body),

  verifyVRChat: (body: { username: string; password: string; totpSecret: string | null }) =>
    post<{ displayName: string | null; userId: string | null; verifiedAt: string }>(
      '/api/onboarding/vrchat',
      body,
    ),

  /**
   * Answers 200 whether or not the connection worked: a failed check is still a successful
   * diagnosis, and the diagnosis is what was asked for.
   */
  testConnection: (body: {
    useProxy?: boolean
    proxyUrl?: string
    proxyUsername?: string
    proxyPassword?: string
  }) => post<ConnectionDiagnosis>('/api/onboarding/connection-test', body),

  listGroups: () => request<GroupCandidates>('/api/onboarding/groups'),

  selectGroup: (body: { groupId: string; name: string | null }) =>
    post<{ groupId: string; name: string }>('/api/onboarding/group', body),

  saveIntegrations: (body: {
    discord?: { botToken?: string; guildId?: string }
    smtp?: {
      host?: string
      port?: number
      username?: string
      password?: string
      fromAddress?: string
      useTls?: boolean
    }
  }) => post<{ discordConfigured: boolean; smtpConfigured: boolean }>(
    '/api/onboarding/integrations',
    body,
  ),

  completeOnboarding: () => post<{ onboardingComplete: boolean }>('/api/onboarding/complete'),

  login: (body: { username: string; password: string }) =>
    post<CurrentUser>('/api/auth/login', body),

  logout: () => post<void>('/api/auth/logout'),

  me: () => request<CurrentUser>('/api/auth/me'),

  /**
   * Cost and capacity are what-if inputs answered against, never stored — nothing in Modbot
   * behaves differently for having been told, so the browser owns that state.
   */
  dataSettings: (budget?: { costPerGbMonth?: number; capacityBytes?: number }) => {
    const q = new URLSearchParams()
    if (budget?.costPerGbMonth) q.set('costPerGbMonth', String(budget.costPerGbMonth))
    if (budget?.capacityBytes) q.set('capacityBytes', String(budget.capacityBytes))
    const query = q.toString()
    return request<DataSettings>(`/api/settings/data${query ? `?${query}` : ''}`)
  },

  setRetention: (body: {
    moderationFactRetentionDays: number
    presenceFactRetentionDays: number
  }) => request<typeof body>('/api/settings/retention', {
    method: 'PUT',
    body: JSON.stringify(body),
  }),
}
