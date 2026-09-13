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
 * How certain a fact's time is.
 *
 * `Window` means the server knows only that it happened between `occurredAt` and
 * `occurredBefore` — a sync noticed a change between two polls. Rendering that as an instant
 * invents precision Modbot does not have, so the two are never displayed the same way.
 */
export type TimePrecision = 'Exact' | 'Window'

/** Which of the two separately-gated logs an entry belongs to. */
export type AuditCategory = 'Moderation' | 'Operational'

export type AuditEntry = {
  id: number
  occurredAt: string
  occurredBefore: string | null
  observedAt: string
  precision: TimePrecision
  type: string
  category: AuditCategory
  source: string
  subjectPlatform: string
  subjectId: string
  actorPlatform: string | null
  actorId: string | null
  actorName: string | null
  worldId: string | null
  instanceId: string | null
  description: string | null
  data: unknown
}

export type AuditCursor = { occurredAt: string; id: number }

export type AuditCoverage = {
  oldestFact: string | null
  firstObservedAt: string | null
  backfillComplete: boolean
}

export type AuditPage = {
  entries: AuditEntry[]
  next: AuditCursor | null
  coverage: AuditCoverage
}

export type AuditActor = { platform: string; id: string; name: string | null; actions: number }

export type AuditFilters = {
  types: { value: string; label: string; category: AuditCategory }[]
  sources: string[]
  actors: AuditActor[]
  canViewModeration: boolean
  canViewOperational: boolean
}

export type AuditRequest = {
  type?: string[]
  source?: string[]
  subject?: string
  actor?: string
  from?: string
  to?: string
  limit?: number
  before?: AuditCursor | null
}

export type BanEntry = {
  subjectPlatform: string
  subjectId: string
  status: 'Banned' | 'Unbanned'
  bannedAt: string | null
  bannedBefore: string | null
  unbannedAt: string | null
  actorPlatform: string | null
  actorId: string | null
  actorName: string | null
  description: string | null
  source: string | null
}

/**
 * The window the ban list actually covers.
 *
 * Not decoration. The list is derived from VRChat's audit log, so it holds the bans Modbot
 * watched happen and no others — and nothing in a list of real rows signals that. A moderator who
 * reads an absence as "not banned" would be acting on a false negative, so this travels with
 * every response and the screen states it permanently.
 */
export type BanCoverage = {
  earliestRecord: string | null
  latestRecord: string | null
  firstSyncedAt: string | null
  backfillComplete: boolean
  lastPolledAt: string | null
  bannedCount: number
  unbannedCount: number
}

export type BanList = {
  bans: BanEntry[]
  total: number
  offset: number
  coverage: BanCoverage
}

export type DayValue = { day: string; value: number }

export type MetricSeries = {
  metric: string
  label: string
  note: string | null
  points: DayValue[]
}

export type ModeratorActivity = {
  dimension: string
  platform: string
  actorId: string
  name: string | null
  actions: number
}

export type ActionTypeSeries = {
  type: string
  label: string
  total: number
  points: DayValue[]
}

/**
 * Two ranges, not one.
 *
 * Rollups are never aged out; facts can be, where an operator set a retention window. Even with
 * nothing pruned the two start in different places, because the audit-log backfill walks history
 * backwards while the rollup job only folds forward. One date picker shown over both would claim
 * they were the same range.
 */
export type MetricsCoverage = {
  rollupFirstDay: string | null
  rollupLastDay: string | null
  factFirstDay: string | null
  factLastDay: string | null
  retentionConfigured: boolean
  moderationFactRetentionDays: number
  presenceFactRetentionDays: number
}

export type Metrics = {
  from: string
  to: string
  series: MetricSeries[]
  memberCount: DayValue[]
  moderators: ModeratorActivity[]
  actionsByType: ActionTypeSeries[]
  coverage: MetricsCoverage
  generatedAt: string
}

/**
 * `Working` / `WaitingOnPurpose` / `NeedsOperator` / `NotConfigured`.
 *
 * The server decides this, not the browser. A cold stop and a WAF block look identical from
 * outside and mean opposite things — one is correct behaviour recovering on its own, the other
 * needs a proxy and never clears — and that judgement should exist once.
 */
export type GatePosture = 'Working' | 'WaitingOnPurpose' | 'NeedsOperator' | 'NotConfigured'

export type GateHealth = {
  state: string
  posture: GatePosture
  headline: string
  coldStoppedBuckets: number
  coldStopEndsAt: string | null
  alertingBuckets: number
}

export type BucketHealth = {
  name: string
  endpointClass: string
  resourceId: string | null
  effectiveRatePerSecond: number
  budgetMultiplier: number
  isColdStopped: boolean
  stoppedUntil: string | null
  alerting: boolean
  rateLimitHits: number
  lastRateLimitedAt: string | null
}

export type CadenceReport = {
  intervalSeconds: number
  reason: string
  consecutiveQuietPolls: number
  decidedAt: string
}

export type SyncRunSummary = {
  outcome: string
  at: string
  durationSeconds: number
  summary: string
}

export type UnmappedEvent = {
  eventType: string
  count: number
  firstSeen: string
  lastSeen: string
  sampleEntryId: string | null
  sampleDescription: string | null
}

export type VocabularyReport = {
  checkedAt: string
  declared: string[]
  unmapped: string[]
  missingPrimary: string[]
  unusedAliases: string[]
  hasProblem: boolean
}

export type SyncHealth = {
  gate: GateHealth
  buckets: BucketHealth[]
  syncRunningInThisProcess: boolean
  auditLogCadence: CadenceReport | null
  lastAuditLogRun: SyncRunSummary | null
  lastGroupInfoRun: SyncRunSummary | null
  auditLogPolledAt: string | null
  groupInfoPolledAt: string | null
  auditLogBackfillComplete: boolean
  auditLogSyncedThrough: string | null
  groupConfigured: boolean
  unmappedAuditEvents: UnmappedEvent[]
  vocabulary: VocabularyReport | null
  now: string
}

export type SyncSettings = {
  auditLog: {
    minIntervalSeconds: number
    maxIntervalSeconds: number
    pacingFloorSeconds: number
    quietBackoff: number
    jitterFraction: number
    pageSize: number
    maxPagesPerRun: number
    overlapSeconds: number
    backfill: boolean
    maxBackfillPages: number
  }
  groupInfo: {
    intervalSeconds: number
    retryIntervalSeconds: number
    rateLimitedIntervalSeconds: number
    pacingFloorSeconds: number
    jitterFraction: number
  }
  editable: boolean
  editableExplanation: string
  running: boolean
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

  /**
   * Read-only in this build. The endpoint says why in `editableExplanation` rather than the SPA
   * deciding — a control whose value is silently discarded is worse than no control.
   */
  syncSettings: () => request<SyncSettings>('/api/settings/sync'),

  /**
   * The merged timeline.
   *
   * `before` is the previous page's `next`, sent back whole. Both halves travel because VRChat's
   * audit entries share timestamps freely: a cursor on time alone drops every entry that fell in
   * the same second as the page boundary.
   */
  audit: (query: AuditRequest = {}) => {
    const q = new URLSearchParams()
    query.type?.forEach((t) => q.append('type', t))
    query.source?.forEach((s) => q.append('source', s))
    if (query.subject) q.set('subject', query.subject)
    if (query.actor) q.set('actor', query.actor)
    if (query.from) q.set('from', query.from)
    if (query.to) q.set('to', query.to)
    if (query.limit) q.set('limit', String(query.limit))
    if (query.before) {
      q.set('beforeOccurredAt', query.before.occurredAt)
      q.set('beforeId', String(query.before.id))
    }
    const search = q.toString()
    return request<AuditPage>(`/api/audit${search ? `?${search}` : ''}`)
  },

  auditFilters: () => request<AuditFilters>('/api/audit/filters'),

  bans: (query: { offset?: number; limit?: number; includeUnbanned?: boolean } = {}) => {
    const q = new URLSearchParams()
    if (query.offset) q.set('offset', String(query.offset))
    if (query.limit) q.set('limit', String(query.limit))
    if (query.includeUnbanned === false) q.set('includeUnbanned', 'false')
    const search = q.toString()
    return request<BanList>(`/api/audit/bans${search ? `?${search}` : ''}`)
  },

  metrics: (days: number) => request<Metrics>(`/api/metrics?days=${days}`),

  gateHealth: () => request<GateHealth>('/api/health/gate'),

  syncHealth: () => request<SyncHealth>('/api/health/sync'),
}
