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
  | 'LinkVRChat'
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
  /** Whether the signed-in account has linked its VRChat account. False when nobody is signed in. */
  vrChatLinked: boolean
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
    /** The channel moderation events are posted to, or null. */
    discordLogChannelId: string | null
    /** The event types posted there now -- the defaults when nothing was chosen. */
    discordLogEventTypes: string[]
    /** Everything that can be chosen, in display order, with labels. */
    discordLogEventChoices: { type: string; label: string }[]
    smtpConfigured: boolean
    smtpHost: string | null
    /** The saved public address, or null. The only thing an emailed link is built from. */
    publicAddress: string | null
    /** What the platform says it is, for the form to prefill. A person confirms it. */
    publicAddressSuggestion: string | null
  }
}

/**
 * The signed-in account.
 *
 * Gate on `permissionNames`, never on `permissions`: the bitfield is 64 bits wide and
 * Administrator is bit 62, which a JavaScript number cannot carry alongside any other bit.
 */
export type CurrentUser = {
  id: string
  username: string
  permissions: number
  permissionNames: string[]
  roles: string[]
  vrChatLinked: boolean
  vrChatUserId: string | null
  vrChatDisplayName: string | null
  email: string | null
  discordUserId: string | null
}

export type PermissionInfo = {
  name: string
  value: number
  label: string
  description: string
  group: string
}

export type RoleView = {
  id: string
  name: string
  description: string
  permissionNames: string[]
  isBuiltIn: boolean
  /** Administrator: always everything, nothing editable. */
  locked: boolean
  userCount: number
}

export type RolesResponse = { roles: RoleView[]; permissions: PermissionInfo[] }

export type RoleRef = { id: string; name: string }

export type UserSummary = {
  id: string
  username: string
  roles: RoleRef[]
  permissionNames: string[]
  isDisabled: boolean
  vrChatLinked: boolean
  vrChatUserId: string | null
  vrChatDisplayName: string | null
  email: string | null
  discordUserId: string | null
  createdAt: string
  lastLoginAt: string | null
}

/**
 * A link that was just made. Shown once — the server keeps only a hash.
 *
 * `url` is null until a public address is saved; the browser then builds it from `path` and its
 * own origin, which is fine for a link the administrator copies from a page they are already on.
 */
export type LinkCreated = { id: string; path: string; url: string | null; expiresAt: string }

export type PendingInvite = {
  id: string
  createdBy: string
  roles: string[]
  createdAt: string
  expiresAt: string
}

export type InviteView = {
  usable: boolean
  reason: string | null
  invitedBy: string | null
  roles: string[]
  expiresAt: string | null
}

export type ResetView = { usable: boolean; reason: string | null; username: string | null }

export type ForgotPasswordWays = { available: boolean; ways: string[]; reason: string | null }

export type PendingLink = { vrChatUserId: string; code: string; expiresAt: string; checksLeft: number }

export type VRChatLinkStatus = {
  linked: boolean
  vrChatUserId: string | null
  vrChatDisplayName: string | null
  linkedAt: string | null
  pending: PendingLink | null
  profileUrl: string
}

export type LinkCheckResult = { linked: boolean; message: string; status: VRChatLinkStatus }

export type PublicAddressView = { publicAddress: string | null; suggestion: string | null }

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

/**
 * A one-time code for pairing the desktop client. Shown to nobody: the pairing page wraps it into
 * a `modbot-client://` link and a pairing token (see `lib/pairingToken.ts`). Single-use and dead
 * after `expiresAt`, so a link left in a browser history is worthless within minutes.
 */
export type IssuedPairingCode = { code: string; expiresAt: string }

export type StorageHorizon = {
  months: number
  estimatedBytes: number
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
  catchUpComplete: boolean
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
  catchUpComplete: boolean
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

/**
 * Two ranges, not one.
 *
 * Daily totals are never aged out; facts can be, where an operator set a retention window. Even with
 * nothing pruned the two start in different places, because the audit-log catch-up walks history
 * backwards while the daily totals job only folds forward. One date picker shown over both would claim
 * they were the same range. `dailyTotalsUpdatedAt` is how fresh anything served from daily totals is.
 */
export type AnalyticsCoverage = {
  dailyTotalsFirstDay: string | null
  dailyTotalsLastDay: string | null
  dailyTotalsUpdatedAt: string | null
  factFirstDay: string | null
  factLastDay: string | null
  retentionConfigured: boolean
  moderationFactRetentionDays: number
  presenceFactRetentionDays: number
}

export type Person = { platform: string; id: string; name: string | null }

export type RoleSummary = {
  id: string
  name: string | null
  isModerationRole: boolean
  isAddedOnJoin: boolean
  isSelfAssignable: boolean
  granted: number
  revoked: number
}

export type TenureBucket = { label: string; minDays: number; maxDays: number | null; members: number }

export type InviteFunnel = {
  invitesSent: number
  joinedAfterInvite: number
  followUpDays: number
  requestsReceived: number
  requestsApproved: number
  requestsRejected: number
}

export type GroupAnalytics = {
  from: string
  to: string
  memberCount: DayValue[]
  joined: DayValue[]
  left: DayValue[]
  netChange: DayValue[]
  invitesSent: DayValue[]
  requestsReceived: DayValue[]
  roles: RoleSummary[]
  rolesKnownAt: string | null
  tenure: TenureBucket[]
  membersWithKnownTenure: number
  invites: InviteFunnel
  coverage: AnalyticsCoverage
  generatedAt: string
}

export type ActionKind = { metric: string; label: string }

export type ModeratorSummary = {
  who: Person
  total: number
  byKind: Record<string, number>
  lastActiveDay: string | null
}

export type KindSeries = { metric: string; label: string; total: number; points: DayValue[] }

export type CoverageGap = {
  worldId: string
  instanceId: string
  startedAt: string
  endedAt: string | null
  endedBy: 'moderator-arrived' | 'instance-closed' | 'unknown'
  peopleWhenLastModeratorLeft: number
  lastModerator: Person | null
}

export type TeamAnalytics = {
  from: string
  to: string
  kinds: ActionKind[]
  moderators: ModeratorSummary[]
  actionsPerDay: DayValue[]
  actionsPerDayByKind: KindSeries[]
  coverageGaps: CoverageGap[]
  moderatorsRecognised: number
  howModeratorsAreRecognised: string
  instancesWatched: number
  instancesOpenedWithoutAnyWatch: number
  coverage: AnalyticsCoverage
  generatedAt: string
}

export type WorldSummary = {
  worldId: string
  minutesSeen: number
  visitors: number
  visits: number
  instancesOpened: number
  lastSeenAt: string | null
}

export type WorldSeries = { worldId: string; points: DayValue[] }

export type WorldsAnalytics = {
  from: string
  to: string
  worlds: WorldSummary[]
  visitorsPerDay: WorldSeries[]
  presenceReports: number
  coverage: AnalyticsCoverage
  generatedAt: string
}

/** 168 buckets, Monday 00:00 UTC first. The page shifts them to the viewer's clock. */
export type HourOfWeek = { arrivals: number[]; opened: number[] }

export type InstancesAnalytics = {
  from: string
  to: string
  opened: DayValue[]
  closed: DayValue[]
  mostOpenAtOnce: DayValue[]
  mostPeopleInOne: DayValue[]
  typicalMinutesOpen: number | null
  instancesWithBothEnds: number
  instancesOpened: number
  hourOfWeek: HourOfWeek
  coverage: AnalyticsCoverage
  generatedAt: string
}

/**
 * `Working` / `WaitingOnPurpose` / `NeedsOperator` / `NotConfigured`.
 *
 * The server decides this, not the browser. A cold stop and a WAF block look identical from
 * outside and mean opposite things — one is correct behaviour recovering on its own, the other
 * needs a proxy and never clears — and that judgement should exist once.
 */
export type GateStatus = 'Working' | 'WaitingOnPurpose' | 'NeedsOperator' | 'NotConfigured'

export type GateHealth = {
  state: string
  status: GateStatus
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

export type PollRateReport = {
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

export type HistoryHorizonReport = {
  entriesRead: number
  reachedAt: string
}

/**
 * What the profile sync is doing. `waitingByReason` is keyed by tier name -- `SeenInInstance`,
 * `OpenedInModbot`, `SeenInFactLog`, `ProfileIsOld`, `NeverRefreshed` -- highest priority first.
 */
export type UserProfileHealth = {
  knownUsers: number
  neverRefreshed: number
  notFound: number
  oldestRefreshedAt: string | null
  waiting: number
  waitingByReason: Record<string, number>
  refreshingUserId: string | null
  refreshesInLastHour: number
  lastRateLimitedAt: string | null
  countedAt: string | null
}

/**
 * The Discord bot's own account of itself. `NotConfigured` is not a fault: no token is stored.
 * `lastError` is a sentence and never the token.
 */
export type DiscordBotHealth = {
  state: 'NotConfigured' | 'Connecting' | 'Connected' | 'Disconnected' | 'Failed'
  connectedSince: string | null
  lastError: string | null
  lastErrorAt: string | null
  commandsRegistered: number
  logChannelConfigured: boolean
  lastPostedAt: string | null
  postedInThisProcess: number
}

export type SyncHealth = {
  gate: GateHealth
  buckets: BucketHealth[]
  syncRunningInThisProcess: boolean
  auditLogPollRate: PollRateReport | null
  lastAuditLogRun: SyncRunSummary | null
  lastGroupInfoRun: SyncRunSummary | null
  lastUserProfileRun: SyncRunSummary | null
  auditLogPolledAt: string | null
  groupInfoPolledAt: string | null
  userProfilePolledAt: string | null
  auditLogCatchUpComplete: boolean
  auditLogSyncedThrough: string | null
  groupConfigured: boolean
  unmappedAuditEvents: UnmappedEvent[]
  auditLogHistoryHorizon: HistoryHorizonReport | null
  userProfiles: UserProfileHealth | null
  /** Null when no bot is registered in this host at all. */
  discordBot: DiscordBotHealth | null
  now: string
}

/**
 * Modbot's sticky "18+ verified" flag -- what Modbot remembers, not what VRChat shows today.
 *
 * `verified` stays true once any refresh has seen the person as 18+ verified. VRChat users can
 * hide their verification again, so `ageVerificationStatusLastSeen` on the profile can read
 * `hidden` while this reads true; only a moderator clears it, and `source` / `setByUsername`
 * say when that happened.
 */
export type AgeVerifiedFlag = {
  verified: boolean
  since: string | null
  source: 'vrchat' | 'manual' | null
  setByUserId: string | null
  setByUsername: string | null
}

export type RefreshState = {
  pending: boolean
  reason: string | null
  requestedAt: string | null
  inProgress: boolean
  /** A sentence when no refresh will come for a while -- the lane is cold-stopped, or no sync runs here. */
  blocked: string | null
}

/**
 * One person's stored profile, with the age of all of it.
 *
 * `known` false means Modbot has no row at all. `lastRefreshedAt` is the time every profile
 * field was true at; `stale` says it is older than the sync's own threshold. A screen shows
 * neither a bio nor an avatar without the first, and labels it with the second.
 */
export type VRChatUserProfile = {
  userId: string
  known: boolean
  displayName: string | null
  bio: string | null
  status: string | null
  statusDescription: string | null
  pronouns: string | null
  avatarImageUrl: string | null
  avatarThumbnailUrl: string | null
  profilePictureUrl: string | null
  dateJoined: string | null
  tags: string[]
  lastPlatform: string | null
  ageVerificationStatusLastSeen: string | null
  ageVerifiedLastSeen: boolean | null
  eighteenPlus: AgeVerifiedFlag
  firstSeenAt: string | null
  lastSeenAt: string | null
  lastRefreshedAt: string | null
  stale: boolean
  staleAfterSeconds: number
  refreshError: string | null
  refreshErrorAt: string | null
  notFoundAt: string | null
  refresh: RefreshState
  now: string
}

export type RefreshRequestResult = {
  outcome: 'Queued' | 'Promoted' | 'AlreadyQueued' | 'FreshEnough' | 'NotAvailable'
  lastRefreshedAt: string | null
  explanation: string
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
    catchUp: boolean
    maxCatchUpPages: number
  }
  groupInfo: {
    intervalSeconds: number
    retryIntervalSeconds: number
    rateLimitedIntervalSeconds: number
    pacingFloorSeconds: number
    jitterFraction: number
  }
  userProfile: {
    intervalSeconds: number
    pacingFloorSeconds: number
    staleAfterSeconds: number
    recentWindowSeconds: number
    freshEnoughWhenOpenedSeconds: number
    freshEnoughWhenSeenInInstanceSeconds: number
    rateLimitedIntervalSeconds: number
  }
  editable: boolean
  editableExplanation: string
  running: boolean
}

/** Which store holds evidence bytes. Mirrors Modbot.Evidence's own enum. */
export type EvidenceBackendId = 'None' | 'S3' | 'Filesystem' | 'Database'

export type EvidenceStoreState = 'NotConfigured' | 'Healthy' | 'Unavailable' | 'Unreachable'

export type EvidenceDurabilityFinding = 'Durable' | 'Unproven' | 'Unwritable'

/**
 * What the store can do, declared by the server rather than discovered by something failing.
 *
 * The browser never infers a capability from a backend name: a filesystem store that presigned
 * URLs would be indistinguishable from one that did not until an operator hit the path the
 * developer never ran.
 */
export type EvidenceCapabilities = {
  presignedRead: boolean
  presignedWrite: boolean
  rangeRead: boolean
  serverSideCopy: boolean
  directDeliveryAvailable: boolean
  deliveryExplanation: string
}

export type EvidenceHealth = {
  state: EvidenceStoreState
  explanation: string
  expectedStoreId: string | null
  foundStoreId: string | null
  since: string | null
  consecutiveFailures: number
  uploadsAllowed: boolean
  locked: boolean
  storeDescription: string
}

/**
 * What is known about whether a directory survives a restart.
 *
 * `isSuspicion` is the field that matters. A suspicion asks and never blocks — Railway, Fly.io and
 * Render all support mountable volumes, and the operator is the only party who knows whether they
 * mounted one. Proof blocks: an unwritable directory is a fact with nothing to decide about it.
 */
export type EvidenceDurability = {
  finding: EvidenceDurabilityFinding
  message: string
  requiresAcknowledgement: boolean
  acknowledged: boolean
  canProceed: boolean
  isSuspicion: boolean
  acknowledgedBy: string | null
  acknowledgedAt: string | null
  warningShown: string | null
}

export type EvidenceSettings = {
  backend: {
    backend: EvidenceBackendId
    storeId: string | null
    root: string | null
    s3: {
      bucket: string | null
      endpoint: string | null
      accessKeyId: string | null
      region: string | null
      prefix: string | null
      usePathStyle: boolean
    }
    /** Whether a secret is on file. The secret itself never leaves the server. */
    secretStored: boolean
  }
  limits: {
    maxFileBytes: number
    maxReportBytes: number
    maxDeploymentBytes: number
    directDeliveryEnabled: boolean
  }
  capabilities: EvidenceCapabilities
  health: EvidenceHealth
  durability: EvidenceDurability | null
  stored: { count: number; bytes: number; destroyedCount: number }
  acceptedTypes: string[]
  backends: {
    id: EvidenceBackendId
    label: string
    summary: string
    recommended: boolean
    caution: string | null
  }[]
  environmentHint: {
    bucket: string | null
    endpoint: string | null
    region: string | null
    accessKeyId: string | null
    secretAvailable: boolean
  } | null
  durabilityStatement: string
  switchBlockedReason: string | null
}

/**
 * The result of the setup check.
 *
 * A failed round trip is a successful diagnosis, so this arrives with a 200 and `succeeded: false`
 * — the whole value of it is the sentence naming which step failed, and an HTTP status cannot
 * carry that.
 */
export type EvidenceSetup = {
  succeeded: boolean
  failedStep: string | null
  message: string
  storeId: string | null
  requiresAcknowledgement: boolean
  durability: EvidenceDurability | null
}

export type EvidenceBackendInput = {
  backend: EvidenceBackendId
  root?: string
  bucket?: string
  endpoint?: string
  accessKeyId?: string
  secretAccessKey?: string
  region?: string
  prefix?: string
  usePathStyle?: boolean
  /** The warning text that was on screen, echoed back as the operator's "use anyway". */
  acknowledgeWarning?: string
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

const put = <T>(path: string, body: unknown): Promise<T> =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) })

const del = <T>(path: string): Promise<T> => request<T>(path, { method: 'DELETE' })

export const api = {
  onboardingStatus: () => request<OnboardingStatus>('/api/onboarding/status'),

  createAdministrator: (body: {
    username: string
    password: string
    confirmPassword: string
    email: string
  }) => post<CurrentUser>('/api/onboarding/administrator', body),

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
    discord?: { botToken?: string; guildId?: string; logChannelId?: string; logEventTypes?: string[] }
    smtp?: {
      host?: string
      port?: number
      username?: string
      password?: string
      fromAddress?: string
      useTls?: boolean
    }
    publicAddress?: string
  }) => post<{ discordConfigured: boolean; smtpConfigured: boolean; publicAddress: string | null }>(
    '/api/onboarding/integrations',
    body,
  ),

  completeOnboarding: () => post<{ onboardingComplete: boolean }>('/api/onboarding/complete'),

  login: (body: { username: string; password: string }) =>
    post<CurrentUser>('/api/auth/login', body),

  logout: () => post<void>('/api/auth/logout'),

  me: () => request<CurrentUser>('/api/auth/me'),

  // ── The signed-in person's own account ──────────────────────────────────────────────────

  changePassword: (body: { currentPassword: string; newPassword: string; confirmPassword: string }) =>
    put<void>('/api/auth/password', body),

  changeUsername: (body: { username: string; currentPassword: string }) =>
    put<CurrentUser>('/api/auth/username', body),

  /** Null leaves a field alone; an empty string clears it. */
  setOwnContact: (body: { email?: string; discordUserId?: string }) =>
    put<CurrentUser>('/api/auth/contact', body),

  signOutEverywhere: () => post<void>('/api/auth/sign-out-everywhere'),

  vrchatLink: () => request<VRChatLinkStatus>('/api/auth/vrchat-link'),

  startVRChatLink: (userIdOrUrl: string) =>
    post<VRChatLinkStatus>('/api/auth/vrchat-link/start', { userIdOrUrl }),

  checkVRChatLink: () => post<LinkCheckResult>('/api/auth/vrchat-link/check'),

  /** Reports what the deployment can do. Nothing about any account. */
  forgotPasswordWays: () => request<ForgotPasswordWays>('/api/auth/forgot-password'),

  /** Always the same sentence back, whoever asked. */
  forgotPassword: (username: string) =>
    post<{ message: string }>('/api/auth/forgot-password', { username }),

  // ── Invite and reset links, from the side of the person holding one ─────────────────────

  invite: (token: string) => request<InviteView>(`/api/join/${encodeURIComponent(token)}`),

  acceptInvite: (token: string, body: { username: string; password: string; confirmPassword: string }) =>
    post<CurrentUser>(`/api/join/${encodeURIComponent(token)}`, body),

  resetLink: (token: string) => request<ResetView>(`/api/reset/${encodeURIComponent(token)}`),

  useResetLink: (token: string, body: { password: string; confirmPassword: string }) =>
    post<void>(`/api/reset/${encodeURIComponent(token)}`, body),

  // ── Users and roles ─────────────────────────────────────────────────────────────────────

  users: () => request<UserSummary[]>('/api/users'),

  createUser: (body: {
    username: string
    password: string
    confirmPassword: string
    roleIds: string[]
    email?: string
    discordUserId?: string
  }) => post<UserSummary>('/api/users', body),

  setUserRoles: (id: string, roleIds: string[]) => put<UserSummary>(`/api/users/${id}/roles`, { roleIds }),

  disableUser: (id: string) => post<UserSummary>(`/api/users/${id}/disable`),

  enableUser: (id: string) => post<UserSummary>(`/api/users/${id}/enable`),

  setUserContact: (id: string, body: { email?: string; discordUserId?: string }) =>
    put<UserSummary>(`/api/users/${id}/contact`, body),

  createResetLink: (id: string) => post<LinkCreated>(`/api/users/${id}/reset-link`),

  invites: () => request<PendingInvite[]>('/api/invites'),

  createInvite: (roleIds: string[]) => post<LinkCreated>('/api/invites', { roleIds }),

  revokeInvite: (id: string) => del<void>(`/api/invites/${id}`),

  roles: () => request<RolesResponse>('/api/roles'),

  createRole: (body: { name: string; description: string; permissions: string[] }) =>
    post<RoleView>('/api/roles', body),

  updateRole: (id: string, body: { name: string; description: string; permissions: string[] }) =>
    put<RoleView>(`/api/roles/${id}`, body),

  deleteRole: (id: string) => del<void>(`/api/roles/${id}`),

  // ── Settings that the accounts layer added ──────────────────────────────────────────────

  publicAddress: () => request<PublicAddressView>('/api/settings/public-address'),

  setPublicAddress: (publicAddress: string) =>
    put<PublicAddressView>('/api/settings/public-address', { publicAddress }),

  sendTestEmail: (to: string) => post<{ sent: boolean; error: string | null }>('/api/settings/email/test', { to }),

  // ── Desktop client ──────────────────────────────────────────────────────────────────────

  /** Signed-in staff only. A device token can never mint another device token. */
  issuePairingCode: () => post<IssuedPairingCode>('/api/client-devices/pairing-code'),

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

  /**
   * The Analytics section, one page per question (spec 10.1). `query` is `days=30` or `all=true`,
   * built by the pages' shared range control so every page means the same thing by a range.
   */
  groupAnalytics: (query: string) => request<GroupAnalytics>(`/api/analytics/group?${query}`),
  teamAnalytics: (query: string) => request<TeamAnalytics>(`/api/analytics/team?${query}`),
  worldsAnalytics: (query: string) => request<WorldsAnalytics>(`/api/analytics/worlds?${query}`),
  instancesAnalytics: (query: string) => request<InstancesAnalytics>(`/api/analytics/instances?${query}`),

  evidenceSettings: () => request<EvidenceSettings>('/api/settings/evidence'),

  /**
   * The round trip, without saving. Writes a test file, reads it back, compares the bytes, promotes
   * it, reads it again, deletes it, and writes the store marker.
   */
  testEvidenceStore: (body: EvidenceBackendInput) =>
    post<EvidenceSetup>('/api/settings/evidence/test', body),

  /**
   * Saves a backend, and only if it passed the same round trip. Leave `secretAccessKey` out to
   * keep the credential already on file, so fixing a typo in the endpoint does not clear it.
   */
  setEvidenceBackend: (body: EvidenceBackendInput) =>
    request<EvidenceSetup>('/api/settings/evidence/backend', {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  setEvidenceLimits: (body: {
    maxFileBytes: number
    maxReportBytes: number
    maxDeploymentBytes: number
    directDeliveryEnabled: boolean
  }) => request<typeof body>('/api/settings/evidence/limits', {
    method: 'PUT',
    body: JSON.stringify(body),
  }),

  probeEvidenceStore: () => post<EvidenceHealth>('/api/settings/evidence/probe'),

  gateHealth: () => request<GateHealth>('/api/health/gate'),

  syncHealth: () => request<SyncHealth>('/api/health/sync'),

  /**
   * One person's stored profile. The id goes in the query string, never the path: VRChat ids are
   * opaque and a legacy one can contain anything (spec 3.1.1).
   */
  userProfile: (id: string) =>
    request<VRChatUserProfile>(`/api/vrchat-users/profile?id=${encodeURIComponent(id)}`),

  /**
   * Asks for a refresh now. Answers immediately; the fetch happens on the users lane at its
   * own pace, so the caller polls `userProfile` until `lastRefreshedAt` moves. A profile
   * fetched within the last half minute is answered `FreshEnough` and nothing is queued, which
   * is what makes calling this on every open safe.
   */
  requestUserRefresh: (id: string) =>
    post<RefreshRequestResult>(`/api/vrchat-users/refresh?id=${encodeURIComponent(id)}`),

  /** Set or clear the sticky 18+ flag by hand. Needs the EditAgeVerification permission. */
  setAgeVerified: (id: string, body: { verified: boolean; reason?: string }) =>
    request<VRChatUserProfile>(`/api/vrchat-users/age-verified?id=${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
}
