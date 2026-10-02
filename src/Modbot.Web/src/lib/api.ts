/**
 * The typed edge of Modbot's HTTP API.
 *
 * Everything the SPA knows about the server is in this file. Handlers elsewhere get values and
 * errors, never a Response -- which is what keeps "did you remember to check response.ok?" from
 * being a question that has to be answered correctly in thirty places.
 */
import type { TrustRank } from './trustRank'

// Type only, so nothing is imported at run time: `giveaways.ts` imports `http` from this file, and
// a real import either way round would be a cycle. The rule tree is defined there because that is
// where the rule builder lives; auto-invites store the same tree (auto-invites design §3).
import type { GiveawayRule } from './giveaways.ts'

// Type only for the same reason: `calendar.ts` imports `http` from this file.
import type { CalendarDuplicate } from './calendar.ts'

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
  | 'SignInWaiting'
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

/**
 * Whether this deployment is a public demo, and what it is doing.
 *
 * `on` is false everywhere but a demo, so the marker and the reset control simply do not exist on
 * an ordinary deployment. See the demo mode design.
 */
export type DemoStatus = {
  on: boolean
  busy: boolean
  step: string
  done: number
  total: number
  seededAt: string | null
  nextResetAt: string | null
  resetHours: number
}

/**
 * What GET /api/onboarding/status says. `vrChat`, `connection` and `integrations` hold real values
 * only while no administrator exists yet and for an account with Manage settings; for anybody else
 * they come back empty, and only the settings topics that need them (all Manage settings) read them.
 */
export type OnboardingStatus = {
  hasAdministrator: boolean
  authenticated: boolean
  /** Whether the signed-in account has linked its VRChat account. False when nobody is signed in. */
  vrChatLinked: boolean
  onboardingComplete: boolean
  nextStep: OnboardingStep
  vrChat: { username: string | null; displayName: string | null; verifiedAt: string | null; lastSignedInAt: string | null }
  connection: {
    checkedAt: string | null
    proxyUrl: string | null
    proxyUsername: string | null
    proxyPasswordStored: boolean
  }
  group: { id: string; name: string; iconUrl: string | null; bannerUrl: string | null } | null
  integrations: {
    discordConfigured: boolean
    discordGuildId: string | null
    discordInstanceChannelId: string | null
    discordInstanceMessage: string | null
    discordInstanceShowNames: boolean
    smtpConfigured: boolean
    smtpHost: string | null
    /** The saved public address, or null. The only thing an emailed link is built from. */
    publicAddress: string | null
    /** What the platform says it is, for the form to prefill. A person confirms it. */
    publicAddressSuggestion: string | null
  }
  /** Where my.modbot.co is, from MODBOT_MY_URL. */
  myModbotUrl: string
  /** Whether VRChat pictures are loaded through this server. */
  vrchatImagesProxied: boolean
  /** Whether this server has a Modbot Cloud to ask, so the updates checkbox is shown. */
  canSubscribeToUpdates: boolean
}

/** What GET /api/server says about this deployment. Every field is null until it is known. */
export type ServerInfo = {
  name: string | null
  groupId: string | null
  iconUrl: string | null
  bannerUrl: string | null
  ownerEmail: string | null
  version: string | null
  publicAddress: string | null
}

/** Settings → Host & Database → Public address. */
export type ServerSettings = { showOwnerEmail: boolean }

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
  /** The Discord username when the account was proven; null for a typed id. */
  discordUsername: string | null
  /** The person signed in to Discord from their account page to prove `discordUserId`. */
  discordProven: boolean
  /** For a typed, unproven id: the day it stops counting. Null otherwise. */
  discordWorksUntil: string | null
  /**
   * Whether Chat answers: AI is on and Chat is on (Settings → AI). The page list offers Chat
   * only while it is (`offered` in lib/nav.ts); a direct link still opens the page.
   */
  chatOn: boolean
  /**
   * Whether the instance and person popups offer an AI brief: Chat answers and briefs are on
   * (Settings → AI → Chat). The buttons show only while it is and the person holds Use AI chat.
   */
  briefsOn: boolean
  /**
   * The position of the account's highest role, first at 0; null with no role. Compare it with a
   * row's own through `isBelowMe` — Manage users and Manage roles only reach what is below it.
   */
  rank: number | null
  /** Whether events that name this account as host or staff invite it. On for a new account. */
  getsEventInvites?: boolean
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
  /** Where it sits in the order, first at 0. The list comes back in this order. */
  position: number
}

export type RolesResponse = { roles: RoleView[]; permissions: PermissionInfo[] }

export type RoleRef = { id: string; name: string }

export type UserSummary = {
  id: string
  username: string
  roles: RoleRef[]
  permissionNames: string[]
  isDisabled: boolean
  isDeleted: boolean
  vrChatLinked: boolean
  vrChatUserId: string | null
  vrChatDisplayName: string | null
  email: string | null
  discordUserId: string | null
  discordUsername: string | null
  discordProven: boolean
  discordWorksUntil: string | null
  createdAt: string
  lastLoginAt: string | null
  /** The position of their highest role, first at 0; null with no role. */
  rank: number | null
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
  /** Whether this server has a Modbot Cloud to ask, so the updates checkbox is shown. */
  canSubscribeToUpdates: boolean
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

/** The member link page (Discord account linking design §3). No Modbot account involved. */
export type LinkPageStatus = {
  /** The OAuth client and the public address are set. */
  available: boolean
  serverName: string | null
  discord: { userId: string; username: string } | null
  /** `checksLeft` is null before Discord sign-in, when nothing has been counted. */
  pending: { vrChatUserId: string; code: string; expiresAt: string; checksLeft: number | null } | null
  link: { vrChatUserId: string; vrChatDisplayName: string | null; linkedAt: string } | null
  profileUrl: string
}

export type LinkPageCheckResult = { linked: boolean; message: string; status: LinkPageStatus }

/** A VRChat person's Discord side, as the person popup shows it. */
export type DiscordLinkView = {
  id: string
  discordUserId: string
  discordUsername: string
  vrChatUserId: string
  /** The VRChat name Modbot has now, else the one saved with the link. */
  vrChatDisplayName: string | null
  linkedAt: string
  startedFrom: 'discord' | 'vrchat'
  /** The roles Modbot gave and believes the member still holds. */
  roles: { id: string; name: string | null }[]
  notInServer: boolean
  roleError: string | null
}

/** Settings → Discord → Account linking. The client secret is never sent to the browser. */
export type DiscordLinkingSettings = {
  clientId: string | null
  clientSecretStored: boolean
  redirectUrl: string | null
  inviteUrl: string | null
  promptNewMembers: boolean
  backupChannelId: string | null
  linkedRoleId: string | null
  eighteenPlusRoleId: string | null
  /** Members can use /me. Always false in a demo. */
  meCommand: boolean
  available: boolean
}

export type DiscordLinkingSettingsInput = {
  clientId: string
  /** Empty keeps the stored secret, unless the client id changed. */
  clientSecret: string
  removeClientSecret: boolean
  promptNewMembers: boolean
  backupChannelId: string
  linkedRoleId: string
  eighteenPlusRoleId: string
  meCommand: boolean
}

/** One VRChat group role paired with one Discord role (M5 §3). */
export type RolePair = {
  id: string
  vrchatRoleId: string
  vrchatRoleName: string | null
  discordRoleId: string
  discordRoleName: string | null
  /** vrchat, discord or nobody. */
  decides: string
  enabled: boolean
  botCanAssign: boolean
  problem: string | null
}

/** Settings → Discord → Role and ban sync. */
export type DiscordSyncSettings = {
  roleSyncOn: boolean
  banSyncToDiscord: boolean
  banSyncToVRChat: boolean
  /** Whether a Discord ban made by another bot is copied into the group too. */
  banSyncFromBots: boolean
  /** ban or remove. */
  banCopyAction: string
  botCanBanMembers: boolean
  botCanRemoveMembers: boolean
  botCanManageRoles: boolean
  rolesRanAt: string | null
  rolesProblem: string | null
  bansReadAt: string | null
  bansProblem: string | null
  pairs: RolePair[]
  groupRoles: { id: string; name: string }[]
}

export type DiscordSyncSettingsInput = {
  roleSyncOn: boolean
  banSyncToDiscord: boolean
  banSyncToVRChat: boolean
  banSyncFromBots: boolean
  banCopyAction: string
}

export type RolePairInput = {
  vrchatRoleId: string
  discordRoleId: string
  decides: string
  enabled: boolean
}

/** One change a sync would make, or has made. */
export type PlannedChange = {
  /** ban, unban, remove, role-given, role-taken or disagree. */
  what: string
  /** vrchat or discord. */
  platform: string
  vrchatUserId: string | null
  discordUserId: string | null
  name: string | null
  roleName: string | null
  why: string
}

export type SyncPreview = { total: number; changes: PlannedChange[]; problem: string | null }

export type PublicAddressView = { publicAddress: string | null; suggestion: string | null }

/** Settings → VRChat Proxy (VRChat proxy design). */
export type VRChatProxySettings = {
  enabled: boolean
  /** What goes in front of a VRChat path: the public address and `/api/proxy/vrchat/`. */
  baseUrl: string
  publicAddressSet: boolean
  /** Whether pictures are loaded through `/api/files/vrchat`. */
  imagesProxied: boolean
}

/** What VRChat answered through the proxy, whatever the status. */
export type VRChatProxyAnswer = {
  status: number
  contentType: string | null
  /** The body as text. JSON is pretty-printed by the caller, not here. */
  text: string
  /** Which account the answer came from, from the `X-Modbot-Proxy-Account` header. */
  account: string | null
}

/** Whether the group's public instances are listed on modbot.co. */
export type PublicInstancesView = {
  shared: boolean
  /** MODBOT_CLOUD_DISABLED is set, so nothing is sent whatever the switch says. */
  cloudDisabled: boolean
  lastSentAt: string | null
}

/** Whether the calendar's event form may upload a picture to VRChat. */
export type VRChatPictureUploadsView = { on: boolean }

export type CloudStatusView = {
  disabled: boolean
  endpoint: string
  registered: boolean
  lastReportAt: string | null
  lastReportOk: boolean | null
  lastReportProblem: string | null
  /** The usage report switch. MODBOT_CLOUD_DISABLED beats it: see `disabled`. */
  reportOn: boolean
}

export type LinkCodeView = { code: string; expiresInMinutes: number }

/**
 * The newest Modbot release, and whether this server looks for it.
 *
 * MODBOT_CLOUD_DISABLED does not turn this off -- the question sends nothing about the deployment
 * -- so there is no `cloudDisabled` here. `on` is the operator's own switch.
 */
export type UpdateView = {
  running: string
  newest: string | null
  newerAvailable: boolean
  publishedAt: string | null
  notesUrl: string | null
  image: string | null
  tag: string | null
  checkedAt: string | null
  problem: string | null
  on: boolean
}

export type DiscordChannelType = 'text' | 'announcement' | 'forum' | 'media' | 'voice' | 'stage' | 'category'

/** What the bot may do in a channel after overwrites, by Discord's permission names. */
export type DiscordChannelPermissions = {
  viewChannel: boolean
  readMessageHistory: boolean
  sendMessages: boolean
  embedLinks: boolean
  attachFiles: boolean
  manageMessages: boolean
}

export type DiscordChannelPermission = keyof DiscordChannelPermissions

export type DiscordChannel = {
  id: string
  name: string
  type: DiscordChannelType
  categoryId: string | null
  position: number
  nsfw: boolean
  /** Deleted in Discord; kept so a saved setting can still show its name. */
  removed: boolean
  botPermissions: DiscordChannelPermissions
}

export type DiscordChannels = {
  guildId: string | null
  serverName: string | null
  /** When the bot last read every channel and role in one go, or null if it never has. */
  refreshedAt: string | null
  updatedAt: string | null
  botCanViewAuditLog: boolean
  botCanManageRoles: boolean
  channels: DiscordChannel[]
}

export type DiscordRole = {
  id: string
  name: string
  /** 0xRRGGBB, zero for none. */
  color: number
  position: number
  managed: boolean
  everyone: boolean
  botCanAssign: boolean
  removed: boolean
  /** A mention of it by the bot pings: open to mentions, or the bot may mention any role. Missing from an older server. */
  botCanMention?: boolean
}

export type DiscordRoles = {
  guildId: string | null
  serverName: string | null
  refreshedAt: string | null
  updatedAt: string | null
  botCanManageRoles: boolean
  roles: DiscordRole[]
}

/** One rule for sending events to a Discord channel. Empty lists match everyone. */
export type DiscordRoute = {
  id: string
  name: string | null
  channelId: string
  enabled: boolean
  eventTypes: string[]
  /** VRChat accounts. */
  subjectIds: string[]
  subjectDiscordIds: string[]
  /** VRChat accounts. */
  actorIds: string[]
  actorDiscordIds: string[]
  /** Also match events nobody did. */
  actorAutomatic: boolean
  subjectVRChatRoleIds: string[]
  actorVRChatRoleIds: string[]
  actorModbotRoleIds: string[]
}

export type DiscordRouteBody = Partial<Omit<DiscordRoute, 'id'>>

export type DiscordRoutePlatform = 'vrchat' | 'discord'

/**
 * One person's profile fields as they stood at a moment. A null is a field Modbot did not know
 * then, or one that was empty; the facts cannot tell the two apart.
 */
export type ProfileFields = {
  displayName: string | null
  bio: string | null
  statusDescription: string | null
  pronouns: string | null
  avatarImageUrl: string | null
  avatarThumbnailUrl: string | null
  /** The best picture the server has: the profile picture, else the avatar. Never fall back here. */
  profilePictureUrl: string | null
  iconUrl: string | null
  bannerUrl: string | null
  representedGroup: RepresentedGroup | null
  dateJoined: string | null
  tags: string[]
  ageVerificationStatus: string | null
  ageVerified: boolean | null
}

/** The group a person shows on their nameplate. Not a link: it is whatever group they chose, not one Modbot knows. */
export type RepresentedGroup = { groupId: string; name: string; iconUrl: string | null }

/** The profile as it stood after one recorded change. `factId` is the audit log entry that recorded it. */
export type ProfileVersion = {
  factId: number
  at: string
  before: string | null
  source: string
  /** The fields this change touched, under VRChat's own names. */
  changed: string[]
  /** The first sighting: where the record begins. */
  baseline: boolean
  /** The newest version: the profile as stored now. */
  current: boolean
  profile: ProfileFields
}

export type ProfileHistory = { userId: string; known: boolean; versions: ProfileVersion[]; now: string }

/** The bodies VRChat last sent for a person, as stored. */
export type RawProfile = {
  userId: string
  publicProfile: unknown
  publicProfileReadAt: string | null
  user: unknown
  userReadAt: string | null
}

/** What the command palette's search found: one list per kind, empty for a kind this account may not see. */
export type SearchResults = {
  people: {
    userId: string
    displayName: string | null
    avatarUrl: string | null
    /** On the group's member list now. */
    isMember: boolean
    /** Was on the member list and has left it. */
    left: boolean
    /** On the group's ban list now. */
    banned: boolean
  }[]
  discordPeople: { userId: string; displayName: string; username: string; avatarUrl: string | null; inServer: boolean }[]
  worlds: { worldId: string; name: string | null; thumbnailImageUrl: string | null }[]
}

export type DiscordRoutePerson = {
  id: string
  name: string | null
  pictureUrl: string | null
  platform: DiscordRoutePlatform
}

export type DiscordRoutes = {
  routes: DiscordRoute[]
  eventGroups: { name: string; types: { type: string; label: string }[] }[]
  vrChatRoles: { id: string; name: string }[]
  modbotRoles: { id: string; name: string }[]
  /** Names for the people the routes already name. */
  people: DiscordRoutePerson[]
}

/** A channel events are sent to that cannot be posted in. */
export type DiscordChannelProblem = {
  channelId: string
  name: string | null
  /** Discord's names for what the bot lacks there. */
  missing: string[]
  removed: boolean
  lastError: string | null
  lastErrorAt: string | null
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

/**
 * A one-time code for pairing the companion. Shown to nobody: the pairing page wraps it into
 * a `modbot-companion://` link and a pairing token (see `lib/pairingToken.ts`). Single-use and dead
 * after `expiresAt`, so a link left in a browser history is worthless within minutes.
 */
export type IssuedPairingCode = { code: string; expiresAt: string }

/**
 * A companion paired to this server. Your own, or everybody's when you hold Manage users. Removed
 * ones come back with `revokedAt` set; the settings list leaves them out.
 */
export type PairedCompanion = {
  id: string
  companionVersion: string
  platform: string
  issuedAt: string
  lastSeenAt: string | null
  revokedAt: string | null
  ownerId: string
  ownerName: string | null
  /** The position of the owner's highest role, first at 0; null with no role. */
  ownerRank: number | null
}

/** One recorded size per UTC day. `day` is `yyyy-mm-dd`. */
export type StorageDay = { day: string; bytes: number }

/**
 * `confidence` is Insufficient (under a day of data) / Low (under a month) / Good. The estimate
 * is a straight line from `bytes` at `measuredAt`, rising `bytesPerDay`. `history` is the past
 * year of recorded days and starts wherever recording started.
 */
export type DataSettings = {
  retention: {
    moderationFactRetentionDays: number
    presenceFactRetentionDays: number
    discordMessageRetentionDays: number
  }
  storage: {
    bytes: number
    facts: number
    bytesPerFact: number
    factsPerDay: number
    observedDays: number
    confidence: 'Insufficient' | 'Low' | 'Good'
    bytesPerDay: number
    capacityExhausted: string | null
    measuredAt: string
    history: StorageDay[]
  }
  deployment: {
    version: string
    commit: string | null
    branch: string | null
    platform: string
    platformEvidence: string | null
    logFilesWritten: boolean
  }
}

/** The other account a proved link ties to this one. A purge never follows it. */
export type PurgeLinkedAccount = {
  platform: 'VRChat' | 'Discord'
  subjectId: string
  name: string | null
}

/**
 * What removing everything about one person would destroy, and what it would keep.
 *
 * `isMember` is null when Modbot never saw a membership row, and `isBanned` is null for a Discord
 * account — neither is the same as false, so neither is drawn as one.
 */
export type PurgePreview = {
  platform: 'VRChat' | 'Discord'
  subjectId: string
  name: string | null
  isMember: boolean | null
  isBanned: boolean | null
  facts: number
  countedDailyTotals: number
  days: number
  messages: number
  giveawayEntries: number
  giveawayPlaces: number
  importRecords: number
  /** Watches on this account, standing or ended. */
  watches: number
  caseFilesKept: number
  evidenceFilesKept: number
  linkedAccount: PurgeLinkedAccount | null
}

/** What a purge destroyed, and what it kept. */
export type PurgeReceipt = {
  facts: number
  countedDailyTotals: number
  days: number
  messages: number
  giveawayEntries: number
  giveawayPlaces: number
  caseFilesKept: number
  evidenceFilesKept: number
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

/**
 * What a fact is about, so a row knows what clicking its subject should open.
 *
 * Decided by the server from the fact's type, never by looking at the shape of the id: VRChat
 * documents its own target only as "typically a UserID, GroupID, GroupRoleID, or Location".
 */
export type SubjectKind = 'Person' | 'Instance' | 'Group' | 'Role' | 'Account' | 'Other'

export type AuditEntry = {
  id: number
  occurredAt: string
  occurredBefore: string | null
  observedAt: string
  precision: TimePrecision
  type: string
  /** The source's own word for an event Modbot had no name for when it was recorded. */
  typeRaw: string | null
  category: AuditCategory
  source: string
  subjectPlatform: string
  subjectId: string
  subjectKind: SubjectKind
  /** The name stored for the subject now, or null. Never the id dressed up as a name. */
  subjectName: string | null
  actorPlatform: string | null
  actorId: string | null
  actorName: string | null
  /** The subject's VRChat trust rank as stored now, when the subject is a VRChat person whose tags are known. */
  subjectTrustRank: TrustRank | null
  /** The same for the actor. */
  actorTrustRank: TrustRank | null
  worldId: string | null
  worldName: string | null
  instanceId: string | null
  /** Modbot's own id for the instance this happened in, where one matched. Opens the instance popup. */
  modbotInstanceId: string | null
  /** The name the matched instance was opened with, shown in place of `instanceId`. Null when it has none. */
  instanceName: string | null
  description: string | null
  data: Record<string, unknown> | null
  /**
   * The other facts that came from the same decision — a ban's instance kick, a Discord ban's
   * leave, Modbot's own record of the press behind VRChat's record of the result. They are not
   * rows of their own; they are shown inside this entry.
   */
  linked?: AuditEntry[] | null
  /**
   * Whose clients reported this, oldest first. Only a client-reported fact has any: the first is
   * the client whose report became the fact, and the rest reported the same thing afterwards and
   * were folded into it.
   */
  reportedBy?: AuditReporter[] | null
  /** The case file this entry leads to: a ban's write-up, or the case file a case file fact is about. */
  caseFileId?: string | null
  /** The review this entry leads to: a flag's review, or the review a review fact is about. */
  reviewId?: string | null
}

/** The same moderator's entries and the same person's around one entry, newest first, the entry in its place. */
export type AuditAround = {
  byActor: AuditEntry[] | null
  aboutSubject: AuditEntry[] | null
}

/** One moderator's client that reported a fact, and when its report arrived. */
export type AuditReporter = {
  accountId: string
  name: string | null
  at: string
}

export type AuditCursor = { occurredAt: string; id: number }

/**
 * One stretch of one person in one instance, made from the same presence reports the time-in-world
 * figures add up.
 */
export type PersonVisit = {
  /** The arrival, or "already here" when a moderator's client arrived after they did. */
  arrived: AuditEntry
  /** Null when nobody saw them go, or when the leave's entry has since gone. */
  left: AuditEntry | null
  /** Whether a leave ended the visit. */
  seenLeaving: boolean
  /** When they left, or the last report from the instance when nobody saw them go. */
  until: string
  /** The display name they had then. */
  name: string | null
  /** Avatars they were seen changing into, in order, each once. */
  avatars: string[]
  /** The moderators whose clients reported them. */
  seenBy: AuditReporter[]
}

export type PersonVisitsPage = {
  visits: PersonVisit[]
  next: AuditCursor | null
  now: string
}

export type AuditCoverage = {
  oldestFact: string | null
  firstObservedAt: string | null
  catchUpComplete: boolean
}

export type AuditPage = {
  entries: AuditEntry[]
  next: AuditCursor | null
  coverage: AuditCoverage
  /** The server's clock when the page was read, for "Today" and "Yesterday". */
  now: string
}

export type AuditActor = { platform: string; id: string; name: string | null; actions: number }

/** What a person's timeline can be narrowed to; no value is everything (see `AuditShow` on the server). */
export type AuditShow = 'moderation' | 'presence' | 'discord'

export type AuditFilters = {
  /** `shows` says which of the timeline's narrowings the type is in. */
  types: { value: string; label: string; category: AuditCategory; shows: AuditShow[] }[]
  sources: string[]
  actors: AuditActor[]
  canViewModeration: boolean
  canViewOperational: boolean
}

// ── One person, whichever of their accounts a link named ────────────────────────────────

/**
 * What tied one of a person's accounts to the one that was asked about.
 *
 * `asked` is the account the link named. `link` is the Discord account link both sides proved.
 * `account` is an id written on a Modbot account, which nobody proved. Kept apart so a screen
 * cannot imply a tie the data does not hold.
 */
export type FoundBy = 'asked' | 'link' | 'account'

export type PersonSide = { id: string; name: string | null; foundBy: FoundBy }

export type PersonAccount = {
  id: string
  username: string
  foundBy: FoundBy
  roles: string[]
  isDisabled: boolean
  createdAt: string
  lastLoginAt: string | null
}

/**
 * One human being, as the accounts Modbot can tie together.
 *
 * A null side is one Modbot has no record of. `canSeeAccount` tells that apart from a side this
 * account may not read: false means the Modbot account is left out whether or not there is one.
 */
export type PersonView = {
  vrChat: PersonSide | null
  discord: PersonSide | null
  account: PersonAccount | null
  canSeeAccount: boolean
}

export type PersonAsk = { vrchat?: string; discord?: string; account?: string }

/**
 * An AI brief: a summary of the audit log entries an instance's or a person's Activity tab shows,
 * written by the Chat model (AI chat design §14).
 *
 * `text` is lines, each ending with the entry ids it rests on in square brackets, `[#1234, #1240]`.
 * Null when nothing was recorded; the model was not asked. `sources` are the cited ids that were
 * among the entries sent, so only those are links. `builtFrom` says what it was built from, and is
 * kept with the text when the brief is saved as a note.
 */
export type Brief = {
  text: string | null
  sources: number[]
  entries: number
  newest: boolean
  builtFrom: string | null
  callId: string | null
  model: string | null
}

export type AuditRequest = {
  type?: string[]
  /** Types to leave out. The type list is the server's, so "is not" cannot be sent as the rest of it. */
  notType?: string[]
  source?: string[]
  subject?: string
  subjectPlatform?: 'VRChat' | 'Discord' | 'Modbot'
  actor?: string
  actorPlatform?: 'VRChat' | 'Discord' | 'Modbot'
  /**
   * One Modbot account's whole history: facts about it and facts it did, together. The log
   * records the first against the subject and the second against the actor, so either half
   * alone is half the story.
   */
  account?: string
  /**
   * One person's whole history across every account the server can tie to this one: facts about
   * any of them and facts any of them did. Tied with the caller's own permissions.
   */
  person?: string
  /** Which platform `person` is on. The server takes VRChat when it is left out. */
  personPlatform?: 'VRChat' | 'Discord' | 'Modbot'
  /** Only moderation, only presence or only Discord. Left out, everything. */
  show?: AuditShow
  from?: string
  to?: string
  /** Only facts that happened in this world. */
  world?: string
  /** Only facts that happened in an instance with this VRChat number. */
  instance?: string
  category?: AuditCategory
  precision?: TimePrecision
  /** True: facts somebody is named for; false: facts nobody is. */
  hasActor?: boolean
  /** A word or phrase to find in the payload, the subject id or the actor id. */
  q?: string
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

/** One role the group defines, as last read by the group-info producer. */
export type RoleOption = { id: string; name: string | null; members: number }

/** A group member's linked Discord account, and how the stored Discord member list has them. */
export type LinkedDiscord = {
  userId: string
  name: string
  avatarUrl: string | null
  inServer: boolean
  leftAt: string | null
}

/** Whether to narrow a member list to people who linked their accounts. Needs See profiles. */
export type LinkedFilter = 'all' | 'linked' | 'not-linked'

/**
 * How far the swept list can be trusted. `firstSweepComplete` false means the list is partial:
 * however many pages the first sweep has read so far, not the group.
 */
export type SweepCoverage = {
  firstSweepComplete: boolean
  lastSyncedAt: string | null
  sweepInProgress: boolean
  now: string
}

export type MemberListCoverage = SweepCoverage & { memberCount: number }

/**
 * One row of the People page: somebody Modbot has a record of, member or not. `displayName` and
 * `avatarThumbnailUrl` come from the stored profile and are null until the profile sync has
 * fetched one -- the row shows the id then.
 */
export type PersonRow = {
  userId: string
  displayName: string | null
  /** The display name in plain letters, when that differs from it. */
  plainName: string | null
  avatarThumbnailUrl: string | null
  /** Null until the profile's tags have been read. */
  trustRank: TrustRank | null
  eighteenPlus: boolean
  isMember: boolean
  /** When a sweep stopped listing them as a member. Null for a current member and for somebody who was never one. */
  leftAt: string | null
  banned: boolean
  firstSeenAt: string
  lastSeenAt: string
  profileRefreshedAt: string | null
  notFoundAt: string | null
  /** The group roles they hold, or held when they left. Empty for somebody who was never a member. */
  roleIds: string[]
  roleNames: string[]
  /** When they joined the group. Null for somebody who was never a member. */
  joinedAt: string | null
  isRepresenting: boolean
  /** Null when they have not linked, and always null without See profiles. */
  linkedDiscord: LinkedDiscord | null
}

export type PeopleList = {
  people: PersonRow[]
  total: number
  page: number
  pageSize: number
  /** The group's roles, with how many current members hold each. */
  roles: RoleOption[]
  coverage: { known: number; members: number; now: string; memberList: MemberListCoverage }
}

export type PeopleQuery = {
  search?: string
  membership?: 'member' | 'not-member' | 'left' | 'all'
  /** On the group's ban list as it stands. */
  banned?: boolean
  /** Banned from the group at any time, lifted or not. */
  everBanned?: boolean
  profile?: 'fetched' | 'not-fetched'
  eighteenPlus?: boolean
  /** Any of these trust ranks. Nobody whose tags have not been read matches. */
  trustRanks?: string[]
  /** Any of these `last_platform` values, as VRChat sent them. */
  platforms?: string[]
  linked?: 'linked' | 'not-linked' | 'all'
  /** Ever flagged by a moderation rule, dismissed or not. */
  flagged?: boolean
  seenFrom?: string
  seenTo?: string
  /** People holding any of these group roles. */
  roles?: string[]
  /** People holding none of these group roles. */
  notRoles?: string[]
  /** True: people with at least one role; false: people with none. */
  hasRole?: boolean
  representing?: boolean
  /** Only people who joined the group at or after this moment. */
  joinedFrom?: string
  /** Only people who joined the group before this moment. */
  joinedTo?: string
  sort?: 'seen' | 'name' | 'known' | 'joined'
  page?: number
  pageSize?: number
}

export type DiscordMemberRole = { id: string; name: string | null; color: number }

/** One member of the Discord server, current or past. */
export type DiscordMember = {
  userId: string
  username: string
  displayName: string
  /** The display name in plain letters, when that differs from it. */
  plainName: string | null
  globalName: string | null
  nickname: string | null
  avatarUrl: string | null
  isBot: boolean
  joinedAt: string | null
  leftAt: string | null
  roles: DiscordMemberRole[]
  timedOutUntil: string | null
  isPending: boolean
  boostingSince: string | null
  firstSeenAt: string
  updatedAt: string
  /** Null when they have not linked, and always null without See profiles. */
  linkedVRChat: { userId: string; displayName: string | null; avatarUrl: string | null } | null
}

export type DiscordMemberList = {
  members: DiscordMember[]
  total: number
  page: number
  pageSize: number
  coverage: { guildId: string | null; listedAt: string | null; inServer: number; now: string }
  /** The server's roles to filter by, highest first. */
  roles: DiscordRoleOption[]
}

export type DiscordMemberQuery = {
  search?: string
  state?: 'in-server' | 'left' | 'all'
  roles?: string[]
  notRoles?: string[]
  hasRole?: boolean
  linked?: LinkedFilter
  bot?: boolean
  pending?: boolean
  timedOut?: boolean
  boosting?: boolean
  joinedFrom?: string
  joinedTo?: string
  sort?: 'joined' | 'oldest' | 'name'
  page?: number
  pageSize?: number
}

/** One of the server's roles, as a filter: with how many people in the server hold it. */
export type DiscordRoleOption = { id: string; name: string | null; color: number; members: number }

export type DiscordMemberMessage = {
  messageId: string
  sentAt: string
  channelId: string
  channelName: string | null
  threadId: string | null
  threadName: string | null
  text: string
  attachments: { name: string; type: string | null; size: number | null; url: string | null }[]
  embedCount: number
  replyToId: string | null
  editedAt: string | null
  deletedAt: string | null
}

export type DiscordMemberMessages = {
  messages: DiscordMemberMessage[]
  total: number
  page: number
  pageSize: number
}

export type DiscordMemberMetrics = {
  userId: string
  messagesPerDay: DayValue[]
  voiceMinutesPerDay: DayValue[]
  messagesAllTime: number
  voiceMinutesAllTime: number
  firstSeenAt: string | null
  joinedAt: string | null
  leftAt: string | null
  history: { change: 'joined' | 'left'; at: string; before: string | null }[]
  now: string
}

/** One person's membership and ban standing, for the subject pane. `known` false: no sweep has listed them. */
export type MembershipView = {
  userId: string
  known: boolean
  isMember: boolean
  roleIds: string[]
  roleNames: string[]
  joinedAt: string | null
  membershipStatus: string | null
  visibility: string | null
  isRepresenting: boolean
  managerNotes: string | null
  firstSeenAt: string | null
  lastSeenAt: string | null
  leftAt: string | null
  banned: boolean
  bannedAt: string | null
  banLiftedAt: string | null
  members: MemberListCoverage
  bans: GroupBanCoverage
}

/** What Modbot can do to somebody in the VRChat group (M4 §2). */
export type ModerationActionName = 'kick' | 'ban' | 'unban'

/**
 * How a kick, ban or unban went.
 *
 * `done` is true only when VRChat accepted it. Anything else means nothing changed in VRChat,
 * whatever the rest of this says — a moderator must never read a success that has not happened.
 */
export type ModerationActionResult = {
  action: ModerationActionName
  userId: string
  done: boolean
  at: string
  /** The case file a ban wrote or updated. */
  caseId: string | null
  /** What VRChat said when it refused. */
  error: string | null
  /** VRChat rate limited it, or Modbot was already waiting one out. Nothing was retried. */
  rateLimited: boolean
  /** This key had already been used: the answer is the first press's, and nothing was sent again. */
  repeat: boolean
  /**
   * VRChat said there was nothing there to act on — a join request somebody had already answered,
   * say. Nothing failed; the row was out of date.
   */
  gone: boolean
  /** Set when VRChat refused because Modbot's own VRChat account lacks a group permission. */
  missingGroupPermission?: MissingGroupPermission | null
  /** A ban or unban was also made on the person's linked Discord account. */
  discordDone: boolean
  /** What Discord said when it refused. The VRChat action stands whatever this says. */
  discordError: string | null
  /** An unban went through but its case file could not be marked as lifted. */
  caseFileError?: string | null
}

/**
 * VRChat refused a group action because Modbot's own VRChat account lacks a group permission.
 * `permission` is VRChat's id for it, or null when Modbot does not know which one; `roles` are the
 * account's group roles by name, or null when Modbot has not read them.
 */
export type MissingGroupPermission = {
  permission: string | null
  groupId: string
  roles: string[] | null
  said: string | null
}

/**
 * What one press of a confirmation sends.
 *
 * `key` is made once when the dialog opens and travels with every press of its button, so a double
 * click, a retry after a timeout and an impatient reload all produce one action (M4 §4.3).
 */
export type ModerationActionBody = {
  userId: string
  key: string
  reasonIds: string[]
  note: string
}

/**
 * One note about a person: a moderator's own words, kept beside everything else recorded about
 * them (notes design).
 *
 * `id` is the id of the fact the note is stored as, because the fact is the note. `text` is text
 * and is rendered as text — never as markup, and never through the Markdown renderer case files
 * use.
 */
export type Note = {
  id: number
  writtenAt: string
  text: string
  subjectPlatform: string
  subjectId: string
  /** The Modbot account that wrote it. Null for one carried in from another system. */
  authorAccountId: string | null
  authorName: string | null
  imported: boolean
  /** Taken back: it still exists and still shows, it simply no longer stands. */
  takenBack: boolean
  takenBackAt: string | null
  takenBackByName: string | null
  canTakeBack: boolean
  /** An AI brief somebody saved, rather than their own words. */
  writtenByAi: boolean
}

export type NoteList = {
  notes: Note[]
  /** How many still stand. */
  standing: number
  canWrite: boolean
}

/**
 * One watch on a person (watching a person design): a moderator's "keep an eye on them". A watched
 * person is Flagged, and their arrival in one of the group's instances raises a notification.
 */
export type Watch = {
  id: string
  subjectPlatform: 'VRChat' | 'Discord'
  subjectId: string
  subjectName: string | null
  /** Null when the reader may not read the audit log. */
  reason: string | null
  setByName: string
  setAt: string
  /** When it stops on its own. Null: until somebody stops it. */
  endsAt: string | null
  /** When somebody should check on the person again. Null: no follow-up. */
  followUpAt: string | null
  followUpDue: boolean
  /** Not stopped, and its end day has not passed. */
  standing: boolean
  endedAt: string | null
  /** Null when it ran out on its own. */
  endedByName: string | null
  /** May stop it or follow up on it: started it, or may write notes. */
  canChange: boolean
}

export type PersonWatches = { watches: Watch[]; canWrite: boolean; now: string }

export type WatchList = { watches: Watch[]; now: string }

export type StartWatchBody = {
  userId: string
  platform: 'VRChat' | 'Discord'
  reason: string
  endsAt: string | null
  followUpAt: string | null
}

/** One row of the group's ban list, as the ban sweep last read it. */
export type GroupBanRow = {
  userId: string
  displayName: string | null
  /** The display name in plain letters, when that differs from it. */
  plainName: string | null
  avatarThumbnailUrl: string | null
  trustRank: TrustRank | null
  bannedAt: string | null
  firstSeenAt: string
  liftedAt: string | null
  profileRefreshedAt: string | null
}

export type GroupBanCoverage = SweepCoverage & { banCount: number }

export type GroupBanList = {
  bans: GroupBanRow[]
  total: number
  page: number
  pageSize: number
  coverage: GroupBanCoverage
}

export type GroupBanQuery = {
  search?: string
  status?: 'current' | 'lifted' | 'all'
  /** Only bans with a case file that stands, or only those without. Needs ViewProfile; ignored without it. */
  caseFile?: 'any' | 'written' | 'none'
  page?: number
  pageSize?: number
}

/**
 * One ban in the Discord server, standing or lifted. `bannedAt` is null for a ban that was already
 * in place when the bot read the list: Discord's list carries no date.
 */
export type DiscordBanRow = {
  userId: string
  username: string | null
  displayName: string | null
  avatarUrl: string | null
  reason: string | null
  bannedAt: string | null
  firstSeenAt: string
  liftedAt: string | null
}

export type DiscordBanList = {
  bans: DiscordBanRow[]
  total: number
  page: number
  pageSize: number
  coverage: {
    guildId: string | null
    /** When the whole list was last read from Discord. Null until it has been. */
    listedAt: string | null
    /** Whether the bot holds Ban Members, which reading the whole list needs. */
    canRead: boolean
    /** Bans that stand, whatever the filters. */
    standing: number
    now: string
  }
}

/** One person waiting to be let into the group, as VRChat had the queue a moment ago. */
export type JoinRequestRow = {
  userId: string
  displayName: string | null
  /** The display name in plain letters, when that differs from it. */
  plainName: string | null
  avatarThumbnailUrl: string | null
  trustRank: TrustRank | null
  eighteenPlus: boolean
  askedAt: string | null
  /** The group's ban list holds them right now. */
  banned: boolean
  /** The group banned them once and the ban was lifted. */
  bannedBefore: boolean
  /** Modbot has them as a member who left, or was removed. */
  wasMember: boolean
  leftAt: string | null
  /** False when Modbot has never recorded anything about this person at all. */
  known: boolean
}

/**
 * One page of the join queue.
 *
 * `hasMore` rather than a total: VRChat sends no count for this list, so a full page is all a
 * next-page control has to go on.
 */
export type JoinRequestList = {
  requests: JoinRequestRow[]
  page: number
  pageSize: number
  hasMore: boolean
  readAt: string
}

export type JoinRequestQuery = { page?: number; pageSize?: number }

/** What one press of an approve or reject confirmation sends. */
export type JoinRequestAnswerBody = {
  userId: string
  key: string
  reasonIds: string[]
  note: string
}

/** Approve or reject, as the Requests screen names them. */
export type JoinRequestAnswer = 'approve' | 'reject'

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

/**
 * The highest a count reached, and the first moment it was that high. `unsure` when an instance head
 * count in it came from `n_users` because VRChat sent no `userCount`: shown as "80?".
 */
export type PeakCount = { value: number; at: string; unsure: boolean }

/**
 * How many readings a member-count peak rests on. `thin` is true when fewer than half the window's
 * days carry a reading, so the peak is the highest Modbot saw rather than the highest there was.
 */
export type MemberCountCoverage = {
  windowDays: number
  daysWithReadings: number
  readings: number
  thin: boolean
}

export type MemberCountPeaks = {
  members: PeakCount | null
  online: PeakCount | null
  coverage: MemberCountCoverage
}

export type GroupAnalytics = {
  from: string
  to: string
  /** The last day, when it is today by the server's clock and so not over yet; otherwise null. */
  today: string | null
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
  peaks: MemberCountPeaks
  /** Days before Modbot began reading the group's audit log. */
  daysWithoutAuditLog: string[]
  coverage: AnalyticsCoverage
  generatedAt: string
}

/** The group as its own VRChat page shows it, from what the group-info sync last read. */
/** Who can join the group, in VRChat's words. */
export type JoinState = 'open' | 'request' | 'invite' | 'closed'

/** A change to the group's profile on VRChat. A field left out is kept as it is. */
export type GroupProfileEdit = {
  name?: string
  description?: string
  rules?: string
  languages?: string[]
  links?: string[]
  joinState?: JoinState
}

/** Who can see a post: the group's members, or everyone. */
export type PostVisibility = 'group' | 'public'

/** One of the group's posts on VRChat. */
export type GroupPostRow = {
  id: string
  title: string | null
  text: string | null
  authorId: string | null
  /** From the profiles Modbot has already read; null when it has none for the author. */
  authorName: string | null
  imageUrl: string | null
  /** Sent back with an edit so the picture stays. */
  imageId: string | null
  visibility: PostVisibility
  /** Only these roles see it. Empty means every member. */
  roleIds: string[]
  createdAt: string | null
  updatedAt: string | null
}

export type GroupRoleChoice = { id: string; name: string }

export type GroupPostList = {
  posts: GroupPostRow[]
  total: number
  page: number
  pageSize: number
  roles: GroupRoleChoice[]
  readAt: string
}

/** A new post, or the whole of a post being changed (`id` set). */
export type GroupPostBody = {
  id?: string | null
  title: string
  text: string
  visibility: PostVisibility
  roleIds: string[]
  notify?: boolean
  imageId?: string | null
}

/** One of the group's roles on VRChat. */
export type GroupRoleRow = {
  id: string
  name: string | null
  description: string | null
  /** VRChat's ids (`group-bans-manage`; `*` is every permission), including any with no label here. */
  permissions: string[]
  order: number
  /** The role every member has. VRChat does not let it be deleted. */
  isDefault: boolean
  isManagementRole: boolean
  isSelfAssignable: boolean
  requiresTwoFactor: boolean
  /** Whether Modbot's own VRChat account has the role, as last read. */
  heldByModbot: boolean
}

export type GroupRoleList = { roles: GroupRoleRow[]; readAt: string }

/** A new role, or a change to one (`id` set). A field left out is kept. */
export type GroupRoleBody = {
  id?: string | null
  name?: string
  description?: string
  permissions?: string[]
}

/** Somebody the group invited on VRChat who has not answered yet. */
export type GroupInviteRow = { userId: string; displayName: string | null; invitedAt: string | null }

export type GroupInviteList = {
  invites: GroupInviteRow[]
  page: number
  pageSize: number
  /** True when the page came back full: VRChat sends no total. */
  hasMore: boolean
  readAt: string
}

export type GroupGalleryChoice = { id: string; name: string | null; description: string | null; membersOnly: boolean }

export type GroupGalleryImageRow = {
  id: string
  imageUrl: string | null
  /** False while it waits for somebody to approve it. */
  approved: boolean
  submittedById: string | null
  submittedByName: string | null
  createdAt: string | null
}

export type GroupGalleryPage = {
  galleries: GroupGalleryChoice[]
  /** The gallery these images are from; null when the group has none. */
  galleryId: string | null
  images: GroupGalleryImageRow[]
  page: number
  pageSize: number
  hasMore: boolean
  readAt: string
}

export type GroupInfo = {
  id: string | null
  name: string | null
  /** The code VRChat shows beside the name, such as `TESTIN`. */
  shortCode: string | null
  /** The four digits after the code's dot. */
  discriminator: string | null
  iconUrl: string | null
  bannerUrl: string | null
  description: string | null
  rules: string | null
  /** VRChat's language codes, such as `eng`. */
  languages: string[]
  /** Absolute `http` or `https` addresses only. */
  links: string[]
  /** Who can join, in VRChat's words. Null before the first read. */
  joinState: JoinState | null
  /** The newest reading's member count, or null before the first. */
  members: number | null
  online: number | null
  countedAt: string | null
  /** When the group-info sync last read the group. */
  readAt: string | null
  generatedAt: string
}

export type MemberCountRange = 'day' | 'week' | 'month' | 'all'

/** One reading of the group's counts, as VRChat reported them at `at`. */
export type MemberCountPoint = {
  at: string
  members: number
  online: number
  /** A day's last group-info fact from before the first reading, carried over rather than read. */
  carried: boolean
}

/**
 * The member count chart: readings from `from` to `to`, at most one per `stepSeconds`, so never
 * more than about 500 points however long the range.
 */
export type GroupMemberCountSeries = {
  range: MemberCountRange
  from: string
  to: string
  stepSeconds: number
  points: MemberCountPoint[]
  generatedAt: string
  /** UTC days the window touches with nothing behind them. Carried days are not in here. */
  daysWithoutReadings: string[]
}

/**
 * `people` for an action on a person (instance kick, warn, ban, removal, request rejected), `door`
 * for door work and admin (invite, approval, unban, role change). Never added together.
 */
export type ActionGroup = 'people' | 'door'

export type ActionKind = { metric: string; label: string; group: ActionGroup }

export type ModeratorSummary = {
  who: Person
  onPeople: number
  doorAndAdmin: number
  byKind: Record<string, number>
  daysActive: number
  /** Actions on people per day with any; null with none. */
  onPeoplePerDay: number | null
  /** The same over the reviews' last 90 days up to yesterday; null until there is one. */
  usualPerDay: number | null
  lastActiveDay: string | null
}

/** The middle moderator for each number; null below four active moderators. */
export type TeamMiddle = {
  moderators: number
  onPeople: number
  doorAndAdmin: number
  daysActive: number
  onPeoplePerDay: number | null
}

/**
 * Busy hours by hour of the week, 168 buckets each, Monday 00:00 UTC first. An hour is busy when a
 * group instance held at least `people` people; `nobodyOn` had a gap in it, `notSeen` had no
 * companion reporting from the busy instance.
 */
export type CoverWeek = {
  people: number
  savedPeople: number
  busy: number[]
  nobodyOn: number[]
  notSeen: number[]
}

export type QueueName = 'join-requests' | 'flags' | 'reviews'

export type QueueWait = {
  queue: QueueName
  decided: number
  /** Null when nothing was decided in the range. */
  middleMinutes: number | null
  middleMinutesPerDay: DayValue[]
}

export type ActedOnAgain = { people: number; again: number; days: number }

export type BansLifted = {
  bans: number
  /** The bans in the range at least `days` days old: the only ones `liftedWithin` is out of. */
  oldEnough: number
  liftedWithin: number
  days: number
  reasons: { label: string; count: number }[]
  liftedWithoutReason: number
}

export type KindSeries = { metric: string; label: string; total: number; points: DayValue[] }

export type CoverageGap = {
  worldId: string
  instanceId: string
  /** Null while Modbot has only seen the world's id. */
  worldName: string | null
  /** Null when no instance Modbot has a row for was open under that number at the time. */
  modbotInstanceId: string | null
  instanceName: string | null
  startedAt: string
  endedAt: string | null
  endedBy: 'moderator-arrived' | 'instance-closed' | 'unknown'
  peopleWhenLastModeratorLeft: number
  lastModerator: Person | null
}

export type TeamAnalytics = {
  from: string
  to: string
  /** The last day, when it is today by the server's clock and so not over yet; otherwise null. */
  today: string | null
  kinds: ActionKind[]
  /** Holds the audit log permission: sees each moderator's numbers and who left last. */
  canSeeEachModerator: boolean
  /** The caller's own numbers; null when no VRChat account is linked. */
  you: ModeratorSummary | null
  /** Empty unless `canSeeEachModerator`. */
  moderators: ModeratorSummary[]
  moderatorsActive: number
  middle: TeamMiddle | null
  onPeoplePerDay: DayValue[]
  doorAndAdminPerDay: DayValue[]
  actionsPerDayByKind: KindSeries[]
  /** `lastModerator` is null throughout unless `canSeeEachModerator`. */
  coverageGaps: CoverageGap[]
  cover: CoverWeek
  waits: QueueWait[]
  actedOnAgain: ActedOnAgain
  bansLifted: BansLifted
  moderatorsRecognised: number
  instancesWatched: number
  instancesOpenedWithoutAnyWatch: number
  daysWithoutAuditLog: string[]
  coverage: AnalyticsCoverage
  generatedAt: string
}

export type WorldSummary = {
  worldId: string
  /** Null while Modbot has only ever seen the id -- ordinary, not a fault. */
  name: string | null
  authorName: string | null
  thumbnailImageUrl: string | null
  capacity: number | null
  minutesSeen: number
  visitors: number
  visits: number
  lastSeenAt: string | null
  /** The group's instances of this world Modbot saw opened in the range: the rows the Instances page lists. */
  instances: number
  /** Minutes with at least one of them open, overlaps counted once. */
  minutesOpen: number
  /** The most people VRChat counted in any one of them; null when none was counted. */
  mostAtOnce: number | null
  mostAtOnceUnsure: boolean
  lastOpenedAt: string | null
}

export type WorldSeries = { worldId: string; points: DayValue[] }

export type WorldsAnalytics = {
  from: string
  to: string
  /** The last day, when it is today by the server's clock and so not over yet; otherwise null. */
  today: string | null
  worlds: WorldSummary[]
  visitorsPerDay: WorldSeries[]
  presenceReports: number
  /** Days an instance was open and no companion reported from any. */
  daysWithoutPresenceReports: string[]
  coverage: AnalyticsCoverage
  generatedAt: string
}

/** 168 buckets, Monday 00:00 UTC first. The page shifts them to the viewer's clock. */
export type HourOfWeek = { arrivals: number[]; opened: number[] }

/** One instance, as it happened. */
export type InstanceRow = {
  id: string
  location: string
  worldId: string
  worldName: string | null
  worldThumbnailImageUrl: string | null
  vrChatInstanceId: string | null
  /** The name the instance was opened with, shown in place of the number. Null when it has none. */
  instanceName: string | null
  groupAccessType: string | null
  region: string | null
  openedAt: string
  closedAt: string | null
  /** How Modbot noticed the end: `list` (exact) or `time` (it merely went quiet). Null while still open. */
  closedBy: string | null
  /** True when a moderator closed it by hand (VRChat's audit log has a close entry for it). */
  closedByModerator: boolean
  peopleNow: number | null
  /** `peopleNow` came from `n_users` because VRChat sent no `userCount`: shown as "80?". */
  peopleNowUnsure: boolean
  peakPeople: number | null
  /** Only such counts reach `peakPeople`. */
  peakPeopleUnsure: boolean
  minutesOpen: number
  /** How many people the world holds, as its page says. Null until Modbot has read the world. */
  worldCapacity: number | null
  /** The platforms the world has a build for, in VRChat's words. Null until Modbot has read them. */
  worldPlatforms: string[] | null
}

/** How many of one kind of thing happened since the last look at Now, and the fact types counted. */
export type NowChange = {
  kind: 'bans' | 'unbans' | 'kicks' | 'warns' | 'caseFiles' | 'notes' | 'flags' | 'joinRequests' | 'joins' | 'leaves'
  count: number
  types: string[]
}

/** The Now page's "since you last looked". */
export type NowLook = {
  since: string
  /** False on a first look, when `since` is a day ago rather than an earlier look. */
  lookedBefore: boolean
  now: string
  /** Every kind with at least one. Null without permission to read the audit log. */
  changes: NowChange[] | null
}

/** A moderator whose client is in an open instance right now. */
export type LiveWatcher = {
  userId: string
  displayName: string | null
  since: string
}

/**
 * Somebody in a live instance. Exactly one of `arrivedAt` and `hereBefore` is set: `arrivedAt` when a
 * moderator saw them walk in, `hereBefore` when they were already there -- they arrived at some
 * earlier time nobody saw.
 */
export type LivePerson = {
  userId: string
  displayName: string | null
  arrivedAt: string | null
  hereBefore: string | null
  standing: string
  priorActions: number
  flags: string[]
  trustRank: TrustRank | null
}

/** One open group instance on the Live page. */
export type LiveInstance = {
  id: string
  worldId: string
  worldName: string | null
  worldImageUrl: string | null
  vrChatInstanceId: string | null
  /** The name the instance was opened with, shown in place of the number. Null when it has none. */
  instanceName: string | null
  groupAccessType: string | null
  region: string | null
  openedAt: string
  headCount: number | null
  /** `headCount` came from `n_users` because VRChat sent no `userCount`: shown as "80?". */
  headCountUnsure: boolean
  watching: LiveWatcher[]
  /** Empty whenever nobody is watching. */
  people: LivePerson[]
  /** When the last moderator stopped watching; null while somebody is. */
  lastWatchedAt: string | null
  /** Who was there at `lastWatchedAt`. Not "here now". */
  lastSeen: LivePerson[]
  /** How many the world holds, as its page says. Null until Modbot has read the world. */
  worldCapacity: number | null
  /** The platforms the world has a build for, in VRChat's words. Null until Modbot has read them. */
  worldPlatforms: string[] | null
}

/** Somebody in a Discord voice channel. `since` is null when they were already there as the bot came online. */
export type LiveVoiceMember = { userId: string; displayName: string; avatarUrl: string | null; since: string | null }

/** A Discord voice channel with people in it right now. */
export type LiveVoiceChannel = { channelId: string; name: string | null; people: LiveVoiceMember[] }

/**
 * What has happened since the oldest open instance opened. Arrivals are people a moderator's
 * Companion App saw walk in, each counted once; the rest are from the group's audit log.
 */
export type LiveTally = { since: string; arrivals: number; warns: number; kicks: number; bans: number }

export type LiveView = {
  instances: LiveInstance[]
  generatedAt: string
  /** The Discord server's voice channels with somebody in them, in the server's own order. */
  voice: LiveVoiceChannel[] | null
  /** Null when no instance is open. */
  tally?: LiveTally | null
}

/** People active on one day, and over the 7 and 30 days ending on it. Distinct people. */
export type ServerActiveDay = { day: string; daily: number; weekly: number; monthly: number }

export type ServerContributor = { who: Person; messages: number; voiceMinutes: number }

/** New members followed for 7 or 30 days after joining. */
export type NewMembersStayed = { days: number; joined: number; stillHere: number; stillActive: number }

/** The Discord server as its own server profile shows it, whatever the range. */
export type ServerProfile = {
  guildId: string | null
  name: string | null
  iconUrl: string | null
  bannerUrl: string | null
  /** When the server was made, read from its id. */
  createdAt: string | null
  /** Discord's own member count, bots included, from the newest reading. */
  members: number | null
  /** How many members Discord counts as online, asked of Discord and kept five minutes. Null when the bot is not connected. */
  online: number | null
  boostCount: number | null
  /** 0 to 3, as Discord gives it. */
  boostLevel: number | null
}

/**
 * What the Discord roles report marks on a role: nobody holds it, another role has its name,
 * another has its permissions and colour, or it belongs to a bot or an integration.
 */
export type RoleFlag = 'no-members' | 'same-name' | 'same-permissions-and-colour' | 'bot-role'

/** One role in the Discord roles report. */
export type RoleReportRow = {
  id: string
  name: string
  /** 0xRRGGBB, zero for none. */
  color: number
  position: number
  /** Null until the member list has been read. */
  members: number | null
  flags: RoleFlag[]
  /** The other roles behind `same-permissions-and-colour`, highest first. */
  samePermissionsAndColourAs: string[]
}

/** The Discord page's Roles tab: every role but @everyone, marked roles first. */
export type RoleReport = {
  guildId: string | null
  membersListedAt: string | null
  rolesReadAt: string | null
  roles: RoleReportRow[]
}

/** One channel in the Discord quiet channels list. */
export type QuietChannelRow = {
  id: string
  name: string
  type: 'text' | 'announcement' | 'forum'
  categoryName: string | null
  /** @everyone cannot see it. */
  staffOnly: boolean
  /** The bot may see it and read its history. */
  canRead: boolean
  /** Its history is still being read back. */
  stillReading: boolean
  /** The newest message in it or its threads. Null when none is known. */
  lastMessageAt: string | null
}

/** The Discord page's Channels tab: quietest first, against the server's clock. */
export type QuietChannelList = {
  guildId: string | null
  now: string
  channels: QuietChannelRow[]
  /** Staff-only channels left out because the caller may not see their names (it takes Change settings). */
  staffOnlyHidden: number
}

/** One number for the last seven days and the seven before them. */
export type WeekPair = { thisWeek: number; lastWeek: number }

/** The last seven days against the seven before, whatever the range. */
export type ServerWeek = {
  from: string
  to: string
  /** People who joined in the week and are still in the server, bots left out. */
  newMembers: WeekPair
  /** Distinct people who sent a message or were in voice. */
  talked: WeekPair
  messages: WeekPair
  voiceMinutes: WeekPair
}

/** How much of the server the bot can read. */
export type ServerReach = { channelsRead: number; channels: number; auditLog: boolean }

/** Who the members are now, bots left out, whatever the range. */
export type MembersNow = {
  members: number
  /** Of them, how many have linked a VRChat account. */
  linked: number
  /** People who joined in the last thirty days, still in or not. */
  joined: number
  /** Of those, how many joined from an account under thirty days old. */
  newAccounts: number
  newAccountsStillHere: number
  tenure: { underAMonth: number; oneToSixMonths: number; sixToTwelveMonths: number; yearOrMore: number }
}

/** A channel in the busiest list. `type` is the stored kind (`text`, `voice`, `forum`…), null when unknown. */
export type ServerChannel = {
  id: string
  name: string | null
  type: string | null
  category: string | null
  removed: boolean
  messages: number
}

export type ServerAnalytics = {
  from: string
  to: string
  /** The last day, when it is today by the server's clock and so not over yet; otherwise null. */
  today: string | null
  server: ServerProfile
  week: ServerWeek
  /** Null when no server is set. */
  reach: ServerReach | null
  membersNow: MembersNow
  memberCount: DayValue[]
  joined: DayValue[]
  left: DayValue[]
  messages: DayValue[]
  voiceMinutes: DayValue[]
  active: ServerActiveDay[]
  busiestChannels: ServerChannel[]
  /** 168 buckets, UTC, Monday 00:00 first. */
  hourOfWeek: { messages: number[] }
  newMembers: NewMembersStayed[]
  bans: DayValue[]
  kicks: DayValue[]
  timeouts: DayValue[]
  messagesRemoved: DayValue[]
  topContributors: ServerContributor[]
  health: { members: number; activeLast30Days: number; wentQuiet: number; quiet: ServerContributor[] }
  /** Days before the bot first read the server. */
  daysWithoutBot: string[]
  /** Days before both the first stored message and the bot's first day. */
  daysWithoutMessages: string[]
  coverage: AnalyticsCoverage
  generatedAt: string
}

/** The day the group's instances held the most people-time. */
export type BusiestDay = { day: string; peopleMinutes: number; mostPeopleAtOnce: number; unsure: boolean }

/** One real clock hour on one real date -- not "Saturdays at 8". */
export type BusiestHour = { startedAt: string; peopleMinutes: number; mostPeopleAtOnce: number; unsure: boolean }

/** The single instance that held the most people at one moment. */
export type BusiestInstance = {
  id: string
  worldId: string
  worldName: string | null
  vrChatInstanceId: string | null
  openedAt: string
  people: number
  at: string
  unsure: boolean
}

/**
 * How much of the window Modbot had a head count for. The denominator is the time instances were
 * open, not the window: a quiet group is not thin coverage. `thin` is true below half of it.
 */
export type InstanceCoverage = {
  windowDays: number
  daysCounted: number
  instancesOpen: number
  instancesCounted: number
  minutesInstancesWereOpen: number
  minutesCounted: number
  thin: boolean
}

/** Peaks from VRChat's own head counts, so they cover instances no companion was in. */
export type InstancePeaks = {
  mostPeopleAtOnce: PeakCount | null
  mostInstancesAtOnce: PeakCount | null
  busiestDay: BusiestDay | null
  busiestHour: BusiestHour | null
  busiestInstance: BusiestInstance | null
  mostPeopleAtOncePerDay: DayValue[]
  peopleMinutesPerDay: DayValue[]
  coverage: InstanceCoverage
}

/** One moment of the staircase: people in the group's instances, and how many had a count. */
export type ActivityPoint = { at: string; people: number; instances: number }

export type InstanceActivitySeries = {
  range: MemberCountRange
  from: string
  to: string
  stepSeconds: number
  points: ActivityPoint[]
  generatedAt: string
  /** UTC days before Modbot started, and days an instance was open and never counted. */
  daysWithoutHeadCounts: string[]
}

export type InstancesAnalytics = {
  from: string
  to: string
  /** The last day, when it is today by the server's clock and so not over yet; otherwise null. */
  today: string | null
  opened: DayValue[]
  /** Instances a moderator closed by hand, per day. Not every instance that ended. */
  closedByHand: DayValue[]
  /** Instances that ended with no close by hand, per day they ended. A day none ended has no row. */
  endedOnTheirOwn: DayValue[]
  mostOpenAtOnce: DayValue[]
  mostPeopleInOne: DayValue[]
  /** Median time open over the group's instances that ended in the range, however they ended. */
  typicalMinutesOpen: number | null
  /** The same, per day an instance ended. A day nothing ended has no row. */
  typicalMinutesOpenPerDay: DayValue[]
  instancesWithBothEnds: number
  instancesOpened: number
  openNow: InstanceRow[]
  recent: InstanceRow[]
  hourOfWeek: HourOfWeek
  peaks: InstancePeaks
  presenceReports: number
  daysWithoutAuditLog: string[]
  daysWithoutHeadCounts: string[]
  daysWithoutPresenceReports: string[]
  /** Days before Modbot started collecting anything about the group as it happened. */
  daysBeforeModbot: string[]
  coverage: AnalyticsCoverage
  generatedAt: string
}

/** What presence reports say about a place. Bounded by who was watching. */
export type PlaceCounts = {
  /** People-time, summed across everybody — not wall-clock. */
  minutesSeen: number
  visitors: number
  arrivals: number
  lastSeenAt: string | null
}

/** One person's own presence figures, over all of recorded history. */
export type PersonCounts = {
  minutesSeen: number
  worlds: number
  instances: number
  arrivals: number
  firstSeenAt: string | null
  lastSeenAt: string | null
}

export type PersonSeen = {
  userId: string
  displayName: string | null
  minutesSeen: number
  arrivals: number
  firstSeenAt: string
  lastSeenAt: string
}

/** One world, its instances and how busy it was. `known` is false when only the id was ever seen. */
export type WorldView = {
  worldId: string
  known: boolean
  name: string | null
  description: string | null
  authorId: string | null
  authorName: string | null
  imageUrl: string | null
  thumbnailImageUrl: string | null
  capacity: number | null
  recommendedCapacity: number | null
  tags: string[]
  releaseStatus: string | null
  publishedAt: string | null
  updatedAt: string | null
  firstSeenAt: string | null
  lastSeenAt: string | null
  /** When the world page was last read. Null means never — the name is still unknown. */
  lastReadAt: string | null
  readError: string | null
  counts: PlaceCounts
  instances: InstanceRow[]
  instancesTotal: number
  instancesOpenNow: number
  visitorsPerDay: DayValue[]
  instancesPerDay: DayValue[]
  now: string
}

/** One instance, with who was in it. What happened there is the audit log, read narrowed to it. */
export type InstanceView = {
  instance: InstanceRow
  known: boolean
  worldAuthorName: string | null
  worldImageUrl: string | null
  worldCapacity: number | null
  type: string | null
  groupId: string | null
  lastSeenAt: string
  seenInGroupList: boolean
  counts: PlaceCounts
  /** How many of the people seen here are group members now. Zero without ViewAuditLog, like `people`. */
  returningMembers: number
  /** False without ViewAuditLog: who was in an instance is moderation history, the instance itself is not. */
  canSeeWhoWasThere: boolean
  people: PersonSeen[]
  now: string
  /** How many were in it each time the count changed, oldest first. The most recent 2000. */
  headCounts: {
    at: string
    people: number
    userCount: number | null
    memberCount: number | null
    source: string
    nUsers: number | null
    /** `people` is `nUsers` because VRChat sent no `userCount`: shown as "80?". */
    unsure: boolean
    /**
     * Against the reading before: `up`, or fewer at a `kick` recorded about then, or fewer because
     * somebody `left`. Null for the first reading and for one that only changed source.
     */
    change: 'up' | 'kick' | 'left' | null
  }[]
  /**
   * Of the people a moderator's companion saw, how many are group members and how many hold each
   * trust rank, at each moment somebody arrived or left. Empty without ViewAuditLog and while no
   * companion was there. Membership and rank are today's, not the ones held at the time.
   */
  peoplePresent: PeoplePresentPoint[]
}

/** Who a companion saw present at one moment: members, and each trust rank. The last point is all zeros. */
export type PeoplePresentPoint = {
  at: string
  members: number
  visitor: number
  newUser: number
  user: number
  knownUser: number
  trustedUser: number
  legend: number
  nuisance: number
  vrChatTeam: number
  /** A rank Modbot has not read yet: not counted as Visitor. */
  rankUnknown: number
}

/**
 * How one instance compared with the other instances in its world while it was open, from the
 * world's page read every two minutes. Empty `readings` for an instance that closed before those
 * reads began.
 */
export type InstanceWorldView = {
  instanceId: string
  worldId: string
  from: string
  to: string
  readings: WorldReading[]
  /** The busiest of the others, busiest first. At most eight. */
  others: OtherInstance[]
  othersTotal: number
  atPeak: WorldReading | null
  busiestMinutes: number
  truncated: boolean
  /** True while VRChat is being asked for a shown instance's or group's name; asking again soon may name it. */
  namesComing: boolean
}

/** One read of the world's page, and where this instance stood in it. */
export type WorldReading = {
  at: string
  occupants: number | null
  publicOccupants: number | null
  privateOccupants: number | null
  /** The world list's number when it carries this instance (`listed`), its own head count otherwise. */
  people: number | null
  unsure: boolean
  listed: boolean
  /** 1 for the busiest in the world. */
  rank: number | null
  of: number
}

/** Another instance in the same world, as the world's list showed it. */
export type OtherInstance = {
  /** As the list carried it, qualifiers and all. */
  instanceId: string
  number: string | null
  groupId: string | null
  groupAccessType: string | null
  region: string | null
  ownGroup: boolean
  /** Modbot's own id, when it has a row for this instance: what opens its popup. */
  modbotInstanceId: string | null
  name: string | null
  /** Another group's name, once VRChat has been asked. Null for the group's own instances. */
  groupName: string | null
  peak: number
  firstSeenAt: string
  lastSeenAt: string
  readings: { at: string; people: number }[]
}

export type PersonMetrics = {
  userId: string
  /** False when no presence report has ever mentioned them. Not the same as never having been anywhere. */
  known: boolean
  counts: PersonCounts
  recentInstances: InstanceRow[]
  now: string
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
  /** Set while Modbot waits to sign in to VRChat again (foundation spec 4.1.2). */
  signInWait: SignInWait | null
  lastSignedInAt: string | null
  signInsInLastHour: number
  signInLimit: number
}

export type SignInWait = {
  reason: 'RateLimitedByVRChat' | 'SignInLimitReached'
  retryAt: string
  /** Whole seconds left on the server's clock when the answer was read. Count down from this, not from the browser's clock. */
  secondsLeft: number
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

/** The rarer of the two profile reads: the full user object. */
export type UserReadHealth = {
  neverRead: number
  oldestReadAt: string | null
  readsInLastHour: number
  lastRateLimitedAt: string | null
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
  /** The privileged intents Discord refused because they are off in the Developer Portal. */
  missingIntents: string[] | null
}

/**
 * The bot's state alone, for the Integrations page: readable with Change settings, where the rest
 * of `DiscordBotHealth` needs See Modbot's log. Null when this server runs no bot.
 */
export type DiscordBotStateView = { state: DiscordBotHealth['state'] | null }

/** How far the bot has read back through the Discord server's message history. */
export type DiscordReadBackHealth = {
  channels: number
  finished: number
  noAccess: number
  messagesStored: number
  lastError: string | null
  lastErrorAt: string | null
  updatedAt: string | null
}

/**
 * Where a member or ban sweep has got to. `phase` is the service's own word -- sweeping,
 * resting, cold-stopped, retrying, idle -- because "last ran 9m ago" cannot tell a
 * deliberate rest from a stuck producer.
 */
export type SweepHealth = {
  phase: string
  lastCompletedAt: string | null
  startedAt: string | null
  offset: number
  count: number
  pagesWalked: number
  rowsChanged: number
  factsWritten: number
  factsDeduplicated: number
  nextPassAt: string | null
  coldStopped: boolean
  polledAt: string | null
  lastRun: SyncRunSummary | null
}

/**
 * One reading of how hard the machine is working.
 *
 * Every figure is nullable, and null means the host does not let Modbot read it — the disk
 * counters are Linux-only. Null is never drawn as zero: a figure that cannot be read is left out
 * of the screen rather than shown as an idle one.
 */
export type MachineUsagePoint = {
  at: string
  processorPercent: number | null
  memoryBytes: number | null
  diskReadBytesPerSecond: number | null
  diskWrittenBytesPerSecond: number | null
}

export type MachineUsage = {
  sampleSeconds: number
  windowMinutes: number
  processors: number
  memoryLimitBytes: number | null
  now: string
  points: MachineUsagePoint[]
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
  /** Channels an enabled route sends to that cannot be posted in. */
  discordChannelProblems?: DiscordChannelProblem[] | null
  memberSweep: SweepHealth | null
  banSweep: SweepHealth | null
  discordReadBack: DiscordReadBackHealth | null
  /** AI spend limits close to being reached, or reached. */
  aiSpend?: AiSpendWarning[] | null
  /** AI calls over the last hour. Null when there have been none. */
  aiCalls?: AiCallsHealth | null
  /** Emails held under the daily email limit, and emails given up on. */
  email?: EmailHealth | null
  /** Calendar places that failed, instances that did not open, and a missing Manage Events. */
  calendar?: CalendarHealth | null
  /** Moderation rules that stopped themselves after acting far more in an hour than usual. */
  pausedRules?: PausedRule[] | null
  lastUserReadRun?: SyncRunSummary | null
  userReads?: UserReadHealth | null
  /** The log Modbot keeps in its own database, and the copy it sends Modbot Cloud. */
  logs?: LogHealth | null
  /** The last report to Modbot Cloud. Null when this server is set not to talk to Cloud. */
  cloudReport?: CloudReportHealth | null
  /** Null until the group-info poll has read the account's permissions. */
  vrChatGroupPermissions?: VRChatGroupPermissionsHealth | null
  now: string
}

export type LogHealth = {
  storing: boolean
  storedWritten: number
  storedDropped: number
  storedAt: string | null
  storeError: string | null
  storeErrorAt: string | null
  sendingToCloud: boolean
  cloudAllowed: boolean
  cloudRegistered: boolean
  cloudSentAt: string | null
  cloudWaiting: number
  cloudDropped: number
  cloudError: string | null
  cloudErrorAt: string | null
}

export type Contributor = {
  login: string
  url: string
  avatarUrl: string
  contributions: number
}

export type ShowcasePerson = {
  name: string
  link: string
  imageUrl: string
  vrChatGroupId: string | null
  groupImageUrl: string | null
  groupBannerUrl: string | null
}

export type Showcase = {
  /** False when this Modbot has Modbot Cloud turned off, or could not reach it. */
  available: boolean
  contributors: Contributor[]
  sponsors: ShowcasePerson[]
  earlyAdopters: ShowcasePerson[]
}

export type HealthWatchView = {
  check: string
  label: string
  on: boolean
  problem: boolean
  since: string | null
  detail: string | null
}

export type HealthRecipientView = {
  userId: string
  username: string
  email: string | null
  chosen: boolean
}

export type HealthAlertView = {
  quietHours: number
  storageWarnGb: number
  emailConfigured: boolean
  lastCheckedAt: string | null
  watches: HealthWatchView[]
  recipients: HealthRecipientView[]
}

export type LogLine = {
  id: number
  at: string
  level: LogLevel
  message: string
  template: string | null
  source: string | null
  area: string | null
  exception: string | null
  /** Everything else the line carried, as a JSON object in a string. */
  properties: string
}

export type LogLevel = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

export type LogPage = {
  lines: LogLine[]
  /** Pass as `before` for the next page. Null at the end. */
  next: number | null
  now: string
}

export type LogFilters = {
  levels: LogLevel[]
  sources: string[]
  areas: string[]
  stored: number
  oldest: string | null
}

export type LogSettings = {
  keepDays: number
  sendToCloud: boolean
  /** False when MODBOT_CLOUD_DISABLED is set. */
  cloudAllowed: boolean
}

export type LogQuery = {
  level?: LogLevel
  source?: string
  area?: string
  text?: string
  from?: string
  to?: string
  before?: number
  limit?: number
}

export type CloudReportHealth = {
  sentAt: string | null
  ok: boolean | null
  problem: string | null
  registered: boolean
  endpoint: string
  /** The usage report switch. Off means nothing is sent; `sentAt` is the last report before that. */
  on: boolean
}

export type PausedRule = {
  ruleKind: 'termList' | 'topic'
  ruleId: string
  ruleName: string
  pausedAt: string
  reason: string | null
}

/** What AI calls have been doing over the last hour. */
export type AiCallsHealth = {
  calls: number
  errors: number
  timedOut: number
  fallbacks: number
  /** The model answering, named only while the fallback is the one answering. */
  answeringModel: string | null
}

export type CalendarHealth = {
  missingManageEvents: boolean
  problems: {
    eventId: string
    title: string
    place: string
    error: string
    at: string | null
    missingGroupPermission?: MissingGroupPermission | null
  }[]
  /** Places scheduled or open events want that are not set up: `vrchat`, `instance`, `discordEvent`, `channelPost`. */
  notSetUp?: string[] | null
  /** Discord server events, made by anyone, that look like copies of each other. */
  duplicates?: CalendarDuplicate[] | null
}

/** The VRChat group permissions Modbot uses that its own VRChat account lacks. */
export type VRChatGroupPermissionsHealth = {
  groupId: string
  missing: string[]
  roles: string[] | null
}

export type EmailHealth = {
  queued: number
  failed: number
  nextSendAt: string | null
}

export type TestEmailResult = {
  sent: boolean
  error: string | null
  queued?: boolean
  /** When a queued test message should go out; null when other email has no room under the limit. */
  sendsAt?: string | null
}

/** One email on the settings page. Never the body. */
export type EmailQueueRow = {
  id: string
  to: string
  kind: 'account' | 'other'
  queuedAt: string
  state: 'queued' | 'sending' | 'failed' | 'expired'
  attempts: number
  nextAttemptAt: string | null
  error: string | null
}

export type EmailSettings = {
  limitPer24Hours: number
  minimumLimit: number
  sentInLast24Hours: number
  queued: number
  failed: number
  nextSendAt: string | null
  emails: EmailQueueRow[]
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
  /** The display name in plain letters, when that differs from it. */
  plainName: string | null
  bio: string | null
  status: string | null
  statusDescription: string | null
  pronouns: string | null
  avatarImageUrl: string | null
  avatarThumbnailUrl: string | null
  /** The best picture the server has: the profile picture, else the avatar. Never fall back here. */
  profilePictureUrl: string | null
  iconUrl: string | null
  bannerUrl: string | null
  representedGroup: RepresentedGroup | null
  dateJoined: string | null
  tags: string[]
  /** Null until the user read has filled the tags. */
  trustRank: TrustRank | null
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

/**
 * Every number a review was opened on, and the ids of the facts behind them.
 *
 * `same-person` reviews carry `places` and `otherModerators`; `far-above-team` reviews carry
 * `day`, `nextBusiest`, `teamUsualPerDay` and `ownUsualPerDay`. `threshold` is what the numbers
 * were held against, so the page can show its working rather than a verdict.
 */
export type ReviewEvidence = {
  actions: number
  byKind: Record<string, number>
  factIds: number[]
  /** Set only on a review opened by a moderation flag (AI moderation design §19). */
  flagId?: string
  ruleName?: string
  ruleVersion?: number
  term?: string
  target?: string
  matched?: string
  reason?: string | null
  language?: string | null
  picture?: string | null
  pictureUrl?: string | null
  subjectName?: string | null
  /** The AI's opinion on the flag, when a moderator asked for one (AutoMod design §6.3). Advice only. */
  aiOpinion?: 'keep' | 'dismiss' | null
  aiOpinionReason?: string | null
  aiProposedAction?: string | null
  /** Set only on a member's request to delete their data, from /me in Discord. */
  discordUserId?: string
  discordUsername?: string | null
  vrchatUserId?: string | null
  vrchatDisplayName?: string | null
  firstAt: string
  lastAt: string
  threshold: Record<string, number>
  places?: number
  otherModerators?: number
  windowDays?: number
  day?: string
  nextBusiest?: { moderatorId: string; actions: number } | null
  teamUsualPerDay?: number
  teamDays?: number
  ownUsualPerDay?: number | null
  ownActiveDays?: number | null
}

export type ReviewView = {
  id: string
  moderator: Person
  signal: string
  signalLabel: string
  about: string
  aboutPerson: Person | null
  windowStart: string
  windowEnd: string
  summary: string
  evidence: ReviewEvidence
  state: 'Open' | 'Closed'
  openedAt: string
  updatedAt: string
  closedAt: string | null
  closedByUsername: string | null
  note: string | null
  /** "right" or "wrong" for a review opened by a moderation flag; null for every other kind. */
  outcome: 'right' | 'wrong' | null
}

export type ReviewList = {
  reviews: ReviewView[]
  openCount: number
  /** When detection last ran. Null means never -- an empty list then means nothing yet. */
  lastRunAt: string | null
  now: string
}

/** One person's count of being acted on. `status` is decided by the `rule` that travels with it. */
export type RepeatOffenderView = {
  who: Person
  instanceKicks: number
  warns: number
  bans: number
  unbans: number
  removals: number
  rejections: number
  actions: number
  actionsLast30Days: number
  actionsLast90Days: number
  moderators: number
  moderatorsLast90Days: number
  firstActionAt: string
  lastActionAt: string
  lastActionType: string
  lastActionLabel: string
  lastBy: Person | null
  status: 'once' | 'more-than-once' | 'repeat'
  computedAt: string
}

export type RepeatOffenderList = {
  people: RepeatOffenderView[]
  total: number
  offset: number
  rule: string
  lastRunAt: string | null
  now: string
}

export type RepeatOffenderTypeOption = { value: string; label: string; counts: boolean }

export type RepeatOffenderRules = {
  threshold: number
  types: RepeatOffenderTypeOption[]
  lastRunAt: string | null
}

export type FlagRuleAutoModOption = { id: string; kind: 'termList' | 'topic'; name: string; counts: boolean }

/** Settings → Moderation → Flagged: which rules make a person Flagged on the companion and Live. */
export type FlagRules = {
  kicksAndBans: boolean
  /** How many days a lifted ban still counts. Null means always. */
  liftedBansForDays: number | null
  warns: boolean
  warnsAtLeast: number
  nuisance: boolean
  autoMod: boolean
  /** Every AutoMod rule counts, including ones added later. */
  everyAutoModRule: boolean
  autoModRules: FlagRuleAutoModOption[]
}

export type FlagRulesInput = Omit<FlagRules, 'autoModRules'> & { autoModRules: string[] }

/** Settings → Auto-invites. `rules` is the same tree the giveaway rule builder reads and writes. */
export type AutoInvites = {
  enabled: boolean
  minutesInInstance: number
  minimumMinutesInInstance: number
  inviteAgainAfterDays: number
  rules: GiveawayRule
  ruleKinds: string[]
  trustRanks: string[]
  groupRoles: { id: string; name: string }[]
  discordRoles: { id: string; name: string }[]
  moderationFactRetentionDays: number
  presenceFactRetentionDays: number
  /** The saved lists a rule can name. */
  lists: { id: string; name: string }[]
  invitesSent: number
  lastInviteAt: string | null
}

export type AutoInvitesInput = {
  enabled: boolean
  minutesInInstance: number
  inviteAgainAfterDays: number
  rules: GiveawayRule
}

export type SubjectHistory = {
  subjectId: string
  known: boolean
  counts: RepeatOffenderView | null
  rule: string
  lastRunAt: string | null
  now: string
}

export type RefreshRequestResult = {
  outcome: 'Queued' | 'Promoted' | 'AlreadyQueued' | 'FreshEnough' | 'NotAvailable' | 'NotAPerson'
  lastRefreshedAt: string | null
  explanation: string
}

/** How a member or ban sweep is paced: one page per `pageDelaySeconds`, then a rest between sweeps. */
export type SweepSettings = {
  pageDelaySeconds: number
  restSeconds: number
  retryIntervalSeconds: number
  rateLimitedIntervalSeconds: number
  pacingFloorSeconds: number
  jitterFraction: number
  pageSize: number
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
    /** How long after they were last seen somebody who is not a member is still refreshed on a schedule. */
    refreshNonMembersForSeconds: number
    freshEnoughWhenOpenedSeconds: number
    freshEnoughWhenSeenInInstanceSeconds: number
    rateLimitedIntervalSeconds: number
  }
  memberSweep: SweepSettings
  banSweep: SweepSettings
  editable: boolean
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
  }
  capabilities: EvidenceCapabilities
  health: EvidenceHealth
  durability: EvidenceDurability | null
  stored: { count: number; bytes: number; destroyedCount: number }
  acceptedTypes: string[]
  backends: {
    id: EvidenceBackendId
    label: string
    recommended: boolean
  }[]
  environmentHint: {
    bucket: string | null
    endpoint: string | null
    region: string | null
    accessKeyId: string | null
    secretAvailable: boolean
  } | null
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

// ── Ban case files (spec 5.8.3, ban case files design) ─────────────────────────────────────

/** The actions a reason can be offered on. `reject` is turning a join request down. */
export type ReasonUseName = 'ban' | 'kick' | 'unban' | 'reject'

/**
 * One reason on the list moderators pick from. `needsWrittenReason`: "Other" cannot stand alone.
 * `usedFor`: the actions that offer it, at least one.
 */
export type BanReasonView = {
  id: string
  label: string
  description: string
  sortOrder: number
  isActive: boolean
  needsWrittenReason: boolean
  usedFor: ReasonUseName[]
}

/** `reasonAlwaysRequired`: a kick, an unban and a rejection each need a reason too, as a ban always does. */
export type BanReasonList = { reasons: BanReasonView[]; canEdit: boolean; reasonAlwaysRequired: boolean }

/** A reason as picked on a case file, with its current label. `isActive` false: since switched off. */
export type CaseFileReason = { id: string; label: string; isActive: boolean }

export type CaseFileSummary = {
  id: string
  userId: string
  displayName: string | null
  bannedAt: string | null
  auditEntryId: string | null
  authorUsername: string
  reasons: CaseFileReason[]
  createdAt: string
  updatedAt: string
  withdrawn: boolean
  evidenceCount: number
  /** When the ban was lifted from Modbot, or null. */
  liftedAt: string | null
}

export type CaseFileList = { cases: CaseFileSummary[]; total: number; offset: number; now: string }

/**
 * The person's stored profile as it stood when the case file was written.
 *
 * Every field is optional, and that is not tidiness. The snapshot is stored as free-form JSON and
 * handed back as it was written, so a case file written by an older Modbot, restored from a
 * backup, or edited by hand can be missing anything in here. A reader must check.
 */
export type ProfileAtBan = {
  userId?: string | null
  displayName?: string | null
  bio?: string | null
  status?: string | null
  statusDescription?: string | null
  pronouns?: string | null
  avatarImageUrl?: string | null
  avatarThumbnailUrl?: string | null
  profilePictureUrl?: string | null
  iconUrl?: string | null
  bannerUrl?: string | null
  representedGroup?: { groupId?: string | null; name?: string | null; iconUrl?: string | null } | null
  dateJoined?: string | null
  tags?: unknown
  trustRank?: unknown
  lastPlatform?: string | null
  ageVerificationStatus?: string | null
  ageVerified?: boolean | null
  eighteenPlus?: { verified?: boolean | null; since?: string | null; source?: string | null } | null
  firstSeenAt?: string | null
  lastSeenAt?: string | null
  lastRefreshedAt?: string | null
  notFoundAt?: string | null
  raw?: unknown
}

/** The group membership as it stood. Every field optional, for the same reason as `ProfileAtBan`. */
export type MembershipAtBan = {
  isMember?: boolean | null
  membershipId?: string | null
  roleIds?: unknown
  joinedAt?: string | null
  membershipStatus?: string | null
  visibility?: string | null
  isRepresenting?: boolean | null
  managerNotes?: string | null
  firstSeenAt?: string | null
  lastSeenAt?: string | null
  leftAt?: string | null
  raw?: unknown
}

export type BanListEntryAtBan = {
  bannedAt?: string | null
  firstSeenAt?: string | null
  lastSeenAt?: string | null
  liftedAt?: string | null
  raw?: unknown
}

/**
 * The snapshot, and the one thing that can happen to it.
 *
 * It never changes after capture, except that `canCaptureAgain` offers a single recapture when
 * VRChat has answered with a newer profile since -- the first snapshot is then kept in the fact
 * log. `explanation` is the server's short line: "Taken 10 Mar 2026 12:05 UTC."
 */
export type CaseSnapshot = {
  profile: ProfileAtBan | null
  membership: MembershipAtBan | null
  banListEntry: BanListEntryAtBan | null
  takenAt: string
  profileRefreshedAt: string | null
  profileAgeSecondsAtCapture: number | null
  recapturedAt: string | null
  profileRefreshedNow: string | null
  canCaptureAgain: boolean
  explanation: string
}

/**
 * One piece of evidence as one case file holds it. `destroyed`: the bytes are gone, the record is
 * not. `takenOffAt` set: the case file let go of it, and this is the line that says so -- the file
 * itself is still in the store, on any other case file that holds it.
 */
export type EvidenceItem = {
  hash: string
  byteSize: number
  contentType: string
  fileName: string | null
  /** Who put it on this case file. */
  uploaderId: string | null
  reportId: string | null
  origin: 'Uploaded' | 'Captured'
  firstStoredAt: string
  destroyed: boolean
  destroyedAt: string | null
  destroyedBy: string | null
  destroyedReason: string | null
  attachedAt: string | null
  takenOffAt: string | null
  takenOffBy: string | null
  /** Where and when it was saved as a clip, by whose companion. Null for anything else. */
  clip: EvidenceClip | null
}

/** A file that is a clip a moderator's companion saved, matched by its SHA-256 when it was attached. */
export type EvidenceClip = {
  savedAt: string
  worldId: string | null
  instanceId: string | null
  savedById: string | null
  savedBy: string | null
  worldName: string | null
}

/**
 * A clip a moderator's companion said it saved while the person on the case file was in the same
 * instance, not on the case file yet. The server has only its fingerprint; the file is on that PC.
 */
export type SavedClip = {
  /** Send back as `clipId` when attaching, so the server checks the file is this clip. */
  id: number
  savedAt: string
  worldId: string
  instanceId: string
  savedById: string
  /** The username of the account the reporting device was paired to: whose PC saved it. */
  savedBy: string
  byteSize: number
  worldName: string | null
}

/** What a destroy answers. A refusal is `destroyed: false` with the case files in the way named in `message`. */
export type EvidenceDestroyed = {
  destroyed: boolean
  blockedByReports: string[]
  message: string
}

/** How evidence bytes travel, in the Settings evidence card's own words, so the two never disagree. */
export type EvidenceDelivery = {
  configured: boolean
  uploadsAllowed: boolean
  storeExplanation: string
  directDelivery: boolean
  maxFileBytes: number
  acceptedTypes: string[]
}

export type CaseFileView = {
  id: string
  userId: string
  displayName: string | null
  groupId: string | null
  auditEntryId: string | null
  banFactId: number | null
  bannedAt: string | null
  bannedBy: Person | null
  authorUserId: string
  authorUsername: string
  createdAt: string
  updatedAt: string
  updatedByUsername: string | null
  reasons: CaseFileReason[]
  writtenReason: string
  withdrawn: boolean
  withdrawnAt: string | null
  withdrawnByUsername: string | null
  withdrawnNote: string | null
  snapshot: CaseSnapshot
  /** Null when the signed-in person may not view evidence. */
  evidence: EvidenceItem[] | null
  evidenceDelivery: EvidenceDelivery
  canEdit: boolean
  canAttach: boolean
  canViewEvidence: boolean
  now: string
  /** Whether Destroy shows on this case file's evidence. */
  canDestroyEvidence: boolean
  /** The unban that lifted the ban, when it was lifted from Modbot. */
  lifted: CaseLift | null
  /** Saved clips this person may be in, not on the case file yet. Null when evidence may not be viewed. */
  clips: SavedClip[] | null
}

/** Why and when a case file's ban was lifted. `unbanFactId` opens the unban's audit log row. */
export type CaseLift = {
  at: string
  byUsername: string | null
  unbanFactId: number | null
  reasons: CaseFileReason[]
  note: string | null
}

export type CaseFileCreated = {
  case: CaseFileView
  refreshOutcome: 'Queued' | 'Promoted' | 'AlreadyQueued' | 'FreshEnough' | 'NotAvailable'
}

export type UnwrittenBan = {
  userId: string
  displayName: string | null
  bannedAt: string
  bannedBefore: string | null
  auditEntryId: string | null
  factId: number
  bannedBy: Person | null
  liftedAt: string | null
}

export type UnwrittenBanList = {
  bans: UnwrittenBan[]
  total: number
  days: number
  since: string
  now: string
}

export type CaseFileLookup = { userId: string; caseId: string | null; count: number }

/** Phase 1 of an evidence upload: where the bytes go. `presigned`: straight to the bucket, not through Modbot. */
export type EvidenceUploadTicket = {
  uploadId: string
  maxBytes: number
  acceptedTypes: string[]
  transferUrl: string
  presigned: boolean
}

export type EvidenceCommitted = { hash: string; byteSize: number; contentType: string }

/** One preset on the AI provider list. `endpoint` is empty for Custom. */
export type AiProviderOption = { id: string; label: string; endpoint: string; recommended: boolean }

/** Settings → AI → Base as stored. The key itself is never sent to the browser. */
export type AiSettings = {
  enabled: boolean
  provider: string
  /** Null when nothing has been saved; the form fills it from the preset. */
  endpoint: string | null
  model: string | null
  apiKeyStored: boolean
  providers: AiProviderOption[]
  acknowledgement: AiAcknowledgement
  /** Tried once when the main model does not answer. Null when there is none. */
  fallbackModel: string | null
  /** How long a call log row is kept, in days. 0 keeps them forever. */
  callLogKeepDays: number
}

/** The one-time confirmation of what member text goes to the provider. */
export type AiAcknowledgement = {
  confirmed: boolean
  at: string | null
  by: string | null
  endpoint: string
  sends: { feature: string; text: string }[]
}

/** One AI call in the call log, counts only. */
export type AiCall = {
  id: string
  at: string
  feature: string
  featureLabel: string
  modelAsked: string
  modelAnswered: string | null
  provider: string | null
  /** The fallback model answered this call. */
  fallback: boolean
  /** `answered`, `timedOut`, `error`, `refused` or `limited`. */
  outcome: string
  outcomeLabel: string
  error: string | null
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  /** Null when the model has no price. */
  cost: number | null
  durationMs: number
  userId: string | null
  username: string | null
  flagged: boolean
  /** The prompt and the answer were kept, so the call can be opened. */
  hasText: boolean
}

export type AiCallLogPage = {
  calls: AiCall[]
  /** Pass as `skip` to read the next page. Null on the last page. */
  next: number | null
  now: string
  models: string[]
  features: string[]
  outcomes: string[]
  keepDays: number
}

export type AiCallDetail = { call: AiCall; prompt: string | null; answer: string | null }

/** What the call log is filtered by. Empty strings mean no filter. */
export type AiCallFilters = {
  feature: string
  outcome: string
  model: string
  from: string
  to: string
  flagged: boolean
}

/** The form's values for the Test button and the model list. Nothing is saved. */
/** Settings → AI → Chat. */
export type AiChatToolSetting = {
  name: string
  label: string
  /** The permissions a person must hold to be offered the tool, as the roles page labels them. */
  needs: string[]
  /** False for a tool that changes something; such a tool is off until switched on. */
  onlyReads: boolean
  enabled: boolean
}

export type AiChatSettings = {
  enabled: boolean
  /** Null means the Base model, `baseModel`. */
  model: string | null
  baseModel: string | null
  instructions: string | null
  maxToolCalls: number
  maxReplyTokens: number
  timeLimitSeconds: number
  aiEnabled: boolean
  tools: AiChatToolSetting[]
  /** Whether the instance and person popups offer an AI brief. */
  briefs: boolean
}

export type AiChatSettingsInput = {
  enabled: boolean
  briefs: boolean
  model: string | null
  instructions: string | null
  maxToolCalls: number
  maxReplyTokens: number
  timeLimitSeconds: number
  tools: Record<string, boolean>
}

/**
 * Money and tokens spent over a stretch of time. `unpricedTokens` are tokens of models with no
 * price: their cost is unknown and is not in `cost`, so a figure with any is only part of the spend.
 */
export type AiSpent = {
  cost: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  unpricedTokens: number
}

/** Per million tokens. A null cached price means cached input costs the same as other input. */
export type AiPrice = {
  model: string
  inputPerMillion: number
  cachedInputPerMillion: number | null
  outputPerMillion: number
}

export type AiFetchedPrice = {
  inputPerMillion: number
  cachedInputPerMillion: number | null
  outputPerMillion: number
  fetchedAt: string
}

/** A model in use, set in settings or priced. The entered price wins over the fetched one. */
export type AiModelPrices = { model: string; entered: AiPrice | null; fetched: AiFetchedPrice | null }

/** Which model box the picker was opened from. */
export type AiModelFeature = 'base' | 'moderation' | 'insights' | 'chat'

/** One model on OpenRouter's list, as the picker shows it. */
export type AiCatalogModel = {
  id: string
  name: string | null
  /** The part of the id before the `/`. */
  maker: string
  contextLength: number | null
  maxOutputTokens: number | null
  inputModalities: string[]
  outputModalities: string[]
  addedAt: string | null
  inputPerMillion: number | null
  cachedInputPerMillion: number | null
  outputPerMillion: number | null
  /** `entered`, `openrouter`, or null when there is no price. */
  priceSource: string | null
  priceVaries: boolean
  free: boolean
  tools: boolean
  structuredOutput: boolean
  imagesIn: boolean
  recommended: boolean
  /** What the feature needs that this model cannot do: `tools`, `structuredOutput`. */
  missing: string[]
  costPerThousandCalls: number | null
}

/** What one call of the feature has used on average, per call. */
export type AiTokenAverage = {
  calls: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
}

export type AiCatalog = {
  feature: AiModelFeature
  needs: string[]
  now: string
  fetchedAt: string | null
  /** Null when there is no usage to work the per-thousand-calls cost out from. */
  average: AiTokenAverage | null
  models: AiCatalogModel[]
  /** The prices the operator entered, for models OpenRouter's list does not carry. */
  prices: AiPrice[]
}

export type AiLimitAppliesTo = 'everyone' | 'feature' | 'role' | 'user'

export type AiLimit = {
  appliesTo: AiLimitAppliesTo
  feature: string | null
  roleId: string | null
  userId: string | null
  name: string | null
  perDay: number | null
  perMonth: number | null
  /** What the limit is compared with. Null for a role limit, which counts each member separately. */
  today: AiSpent | null
  month: AiSpent | null
  /** The month-end estimate, for a limit for everyone or a feature. */
  estimate: AiSpent | null
}

/** A monthly token limit kept from before prices. */
export type AiTokenLimit = { feature: string; monthlyTokens: number; month: AiSpent; estimate: AiSpent }

export type AiFeatureSpend = {
  feature: string
  label: string
  today: AiSpent
  week: AiSpent
  month: AiSpent
  lastMonth: AiSpent
  estimate: AiSpent
}

export type AiLimits = {
  now: string
  today: AiSpent
  month: AiSpent
  spend: AiFeatureSpend[]
  total: AiFeatureSpend
  firstDay: string
  lastDay: string
  days: { day: string; feature: string; cost: number; tokens: number; unpricedTokens: number }[]
  limits: AiLimit[]
  tokenLimits: AiTokenLimit[]
  topChatUsers: { userId: string; username: string | null; month: AiSpent }[]
  prices: AiPrice[]
  models: AiModelPrices[]
  pricesFetchedAt: string | null
  modelsUsed: string[]
  features: { id: string; label: string }[]
  roles: { id: string; name: string }[]
  users: { id: string; name: string }[]
  allowances: AiAllowances
}

export type AiLimitInput = {
  appliesTo: AiLimitAppliesTo
  feature: string | null
  roleId: string | null
  userId: string | null
  perDay: number | null
  perMonth: number | null
}

export type AiTokenLimitInput = { feature: string; monthlyTokens: number }

/** What a team member may use in a month. Null means no limit of that kind. */
export type AiAllowanceAmount = { tokens: number | null; money: number | null }

export type AiMemberAllowance = {
  userId: string
  name: string
  /** Their own allowance, in place of the default. Null when they use the default. */
  own: AiAllowanceAmount | null
  /** What applies to them: their own, or the default. */
  allowance: AiAllowanceAmount
  /** Everything used under this account this month, every feature together. */
  month: AiSpent
  /** Administrator, or allowed to use AI past limits: the allowance does not stop them. */
  pastLimits: boolean
}

export type AiAllowances = {
  default: AiAllowanceAmount
  members: AiMemberAllowance[]
  /** When the month being counted ends and every allowance starts again. */
  resetsAt: string
}

export type AiAllowancesInput = {
  default: AiAllowanceAmount
  members: { userId: string; tokens: number | null; money: number | null }[]
}

/** A limit for everyone or a feature at 80% or more, estimated over, or reached. */
export type AiSpendWarning = {
  appliesTo: 'everyone' | 'feature' | 'tokens'
  feature: string | null
  label: string | null
  period: 'day' | 'month'
  unit: 'money' | 'tokens'
  limit: number
  spent: number
  estimate: number | null
  reached: boolean
  partUnknown: boolean
}

/**
 * A source of an answer: something a tool returned, and where it opens.
 *
 * The first four open a popup; a fact opens the audit log at that entry, a case the case file, a
 * message that person's Discord messages at it, and an event the calendar.
 */
export type ChatReference = {
  kind: 'person' | 'world' | 'instance' | 'discord-person' | 'fact' | 'case' | 'message' | 'event'
  id: string
  label: string | null
  /** For a message: the Discord account whose messages it opens in. */
  author?: string | null
}

export type ChatConversationSummary = { id: string; title: string; updatedAt: string }

export type ChatHome = { available: boolean; model: string | null; conversations: ChatConversationSummary[] }

export type ChatToolCall = { id: string; name: string; label: string; arguments: string }

export type ChatMessage = {
  id: number
  /** The message this one follows. Null for the first message of a conversation. */
  parentId: number | null
  /** Every version of this message, oldest first, this one among them. One entry means one version. */
  versions: number[]
  role: 'user' | 'assistant' | 'tool'
  content: string
  toolCalls: ChatToolCall[]
  toolCallId: string | null
  toolName: string | null
  toolLabel: string | null
  references: ChatReference[]
  worked: boolean | null
  durationMs: number | null
  /** True for a reply that was stopped, or cut off by the time limit, part-written. */
  stopped: boolean
  createdAt: string
}

export type ChatConversation = {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  full: boolean
  messages: ChatMessage[]
}

/** One server-sent event from sending a message, in the order the server sends them. */
export type ChatStreamEvent =
  | { type: 'conversation'; data: ChatConversationSummary }
  | { type: 'message'; data: ChatMessage }
  | { type: 'text'; data: { text: string } }
  | { type: 'tool'; data: { callId: string; name: string; label: string } }
  | { type: 'done'; data: { outcome: string; error: string | null } }

export type AiConnectionInput = {
  provider: string
  endpoint: string
  model?: string
  /** Omitted to use the stored key, which the server sends only to the endpoint it was saved with. */
  apiKey?: string
}

export type AiSettingsInput = AiConnectionInput & {
  enabled: boolean
  removeApiKey?: boolean
  fallbackModel?: string
  callLogKeepDays?: number
}

/** One number for the days an insight covers and the same number for the days before. Null means nothing was recorded. */
export type InsightFigure = { name: string; now: number | null; before: number | null }

export type InsightFigures = {
  kind: string
  firstDay: string
  lastDay: string
  beforeFirstDay: string
  beforeLastDay: string
  figures: InsightFigure[]
  lists: { name: string; items: { name: string; value: number }[] }[]
}

export type InsightKind = 'group' | 'team' | 'instances'

/** An AI-written summary of the group's own figures, stored with the figures it was written from. */
export type Insight = {
  id: string
  kind: InsightKind
  label: string
  firstDay: string
  lastDay: string
  createdAt: string
  startedBy: 'schedule' | 'button'
  requestedBy: string | null
  model: string | null
  /** Null when the attempt failed; `error` says why. */
  text: string | null
  error: string | null
  figures: InsightFigures | null
  discordPostedAt: string | null
  discordError: string | null
}

export type InsightEvery = 'day' | 'week'

export type InsightKindSettings = {
  kind: InsightKind
  label: string
  enabled: boolean
  every: InsightEvery
  /** 0 to 23, in the time zone. */
  hour: number
  /** 0 is Sunday. */
  weekday: number
  discordChannelId: string | null
  last: Insight | null
}

export type AiInsightsSettings = {
  timeZone: string | null
  model: string | null
  baseModel: string | null
  aiOn: boolean
  kinds: InsightKindSettings[]
}

export type AiInsightsSettingsInput = {
  timeZone: string | null
  model: string | null
  kinds: Omit<InsightKindSettings, 'label' | 'last'>[]
}

export type NotificationSeverity = 'critical' | 'warning' | 'information'

export type NotificationLevel = 'off' | 'critical' | 'warning' | 'everything'

/** One thing Modbot decided this account should be told about. */
export type ModbotNotification = {
  id: string
  kind: string
  severity: NotificationSeverity
  title: string
  body: string
  link: string | null
  at: string
  repeats: number
  /** Critical, and it reached this account on no channel at all. */
  waiting: boolean
  seen: boolean
}

export type NotificationChoice = {
  channel: string
  label: string
  level: NotificationLevel
  dailySummary: boolean
  canReach: boolean
}

export type AlertWatcher =
  | 'vrchat-joins'
  | 'discord-joins'
  | 'new-accounts'
  | 'flags'
  | 'actions'
  | 'leaves'
  | 'instances-opened'
  | 'instance-filling'
  | 'instance-unwatched'
  | 'active-drop'

export type AlertSensitivity = 'off' | 'low' | 'normal' | 'high'

/** One time something ran far outside this deployment's own normal. Counts and places only. */
export type Alert = {
  id: string
  watcher: AlertWatcher
  label: string
  /** What the figure counts, in plain words. */
  counts: string
  at: string
  windowStart: string
  windowEnd: string
  now: number
  normal: number
  spread: number
  score: number
  sensitivity: AlertSensitivity
  /** The world or instance, for the two instance watchers. */
  where: string | null
  /** Where in Modbot to look, as a path. */
  link: string | null
  text: string | null
  model: string | null
  dismissedAt: string | null
  dismissedBy: string | null
  discordPostedAt: string | null
  discordError: string | null
}

export type AlertWatchSettings = {
  watcher: AlertWatcher
  label: string
  sensitivity: AlertSensitivity
  last: Alert | null
}

export type AiAlertSettings = {
  discordChannelId: string | null
  quietHours: number
  writeSentence: boolean
  aiOn: boolean
  watchers: AlertWatchSettings[]
}

export type AiAlertSettingsInput = {
  discordChannelId: string | null
  quietHours: number
  writeSentence: boolean
  watchers: { watcher: AlertWatcher; sensitivity: AlertSensitivity }[]
}

/** One API key as the list shows it. Never the key itself. */
export type ApiKeyView = {
  id: string
  name: string
  /** The first characters of the key. */
  start: string
  permissionNames: string[]
  ownerId: string
  ownerName: string | null
  createdAt: string
  expiresAt: string | null
  lastUsedAt: string | null
  revokedAt: string | null
  state: 'active' | 'expired' | 'revoked'
}

/** `grantable` is what the signed-in person may put on a new key: what they hold. */
export type ApiKeysResponse = { keys: ApiKeyView[]; grantable: PermissionInfo[] }

/** `key` is shown once. */
export type CreatedApiKey = { apiKey: ApiKeyView; key: string }

/** Settings → AI → MCP (MCP server design). */
export type McpSettings = {
  enabled: boolean
  /** Where an AI app connects: the public address and `/mcp`. */
  serverUrl: string
  publicAddressSet: boolean
  tools: AiChatToolSetting[]
  /** What an API key made for MCP should carry: the permission names, narrowed to what the caller holds. */
  keyPermissions: string[]
}

/** One AI app connected to the signed-in person's account. */
export type McpConnection = {
  id: string
  clientName: string
  clientUri: string | null
  connectedAt: string
  lastUsedAt: string | null
  expiresAt: string
}

/** What the `/connect` page shows: the app asking, and the tools the person would give it. */
export type McpSignInView = {
  clientName: string
  clientUri: string | null
  redirectHost: string
  tools: { name: string; label: string; needs: string[] }[]
}

export type McpSignInAnswer = {
  clientId: string
  redirectUri: string | null
  state: string | null
  codeChallenge: string | null
  codeChallengeMethod: string | null
  scope: string | null
  resource: string | null
  approve: boolean
}

export type EventTypeOption = { type: string; label: string; category: 'moderation' | 'operational' }

export type WebhookState = 'working' | 'failing' | 'stopped' | 'off'

export type WebhookView = {
  id: string
  name: string
  url: string
  eventTypes: string[]
  subjectIds: string[]
  enabled: boolean
  ownerId: string
  ownerName: string | null
  /** Only the person who set it up, or an administrator, may change it or send a test. */
  canEdit: boolean
  createdAt: string
  lastSuccessAt: string | null
  failingSince: string | null
  lastError: string | null
  nextAttemptAt: string | null
  disabledAt: string | null
  disabledReason: string | null
  state: WebhookState
}

export type WebhooksResponse = {
  webhooks: WebhookView[]
  allowPrivateAddresses: boolean
  canChangeAllowPrivateAddresses: boolean
}

export type WebhookInput = {
  name: string
  url: string
  eventTypes: string[]
  subjectIds: string[]
  enabled: boolean
}

/** `secret` is shown once. */
export type CreatedWebhook = { webhook: WebhookView; secret: string }

export type WebhookDeliveryView = {
  id: number
  eventId: string
  eventType: string
  attemptedAt: string
  attempt: number
  statusCode: number | null
  durationMs: number
  error: string | null
  test: boolean
  outcome: 'delivered' | 'retrying' | 'skipped'
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
  /** The parsed body, for the few answers that carry more than a sentence -- a 409 naming the case file that already exists. */
  readonly detail: unknown

  constructor(status: number, message: string, diagnosis: ConnectionDiagnosis | null, detail: unknown = null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.diagnosis = diagnosis
    this.detail = detail
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

  throw new ApiError(response.status, refusalMessage(body, response.status), null, body)
}

/**
 * The sentence a refusal carries. Most endpoints answer `{ error }`; a few answer with ASP.NET's
 * problem shape (`{ title, detail }`), and reading only the first turned those into a bare status
 * code on screen.
 */
function refusalMessage(body: unknown, status: number): string {
  if (typeof body === 'object' && body !== null) {
    const b = body as { error?: unknown; detail?: unknown; title?: unknown }
    if (b.error !== undefined && b.error !== null) return String(b.error)
    if (typeof b.detail === 'string' && b.detail) return b.detail
    if (typeof b.title === 'string' && b.title) return b.title
  }
  return `The server answered ${status}.`
}

/**
 * Sends a chat message and hands each server-sent event to `onEvent` as it arrives.
 *
 * Not `request`: the answer is a stream, read a chunk at a time so the reply appears as it is
 * written. A refusal before the stream starts (400, 404, 409) still arrives as an ApiError.
 */
async function sendChatMessage(
  body: {
    conversationId: string | null
    text: string
    /** A question of this conversation that `text` is the edited version of. */
    replaceMessageId?: number
    /** Write another reply to this message; `text` is not used. */
    retryAfterMessageId?: number
  },
  onEvent: (event: ChatStreamEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  let response: Response

  try {
    response = await fetch('/api/chat/messages', {
      method: 'POST',
      headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify(body),
      signal,
    })
  } catch (e) {
    if (signal?.aborted) throw e
    throw new ApiError(0, 'Could not reach the Modbot server. Is it still running?', null)
  }

  if (!response.ok || !response.body) {
    const text = await response.text()
    let parsed: unknown = null
    try {
      parsed = text ? JSON.parse(text) : null
    } catch {
      parsed = null
    }
    throw new ApiError(response.status, refusalMessage(parsed, response.status), null, parsed)
  }

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''

  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value

    let end = buffer.indexOf('\n\n')
    while (end >= 0) {
      const block = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)
      end = buffer.indexOf('\n\n')

      let type = ''
      let data = ''
      for (const line of block.split('\n')) {
        if (line.startsWith('event: ')) type = line.slice(7)
        else if (line.startsWith('data: ')) data += line.slice(6)
      }
      if (type && data) onEvent({ type, data: JSON.parse(data) } as ChatStreamEvent)
    }
  }
}

const post = <T>(path: string, body?: unknown): Promise<T> =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

const put = <T>(path: string, body: unknown): Promise<T> =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) })

const del = <T>(path: string): Promise<T> => request<T>(path, { method: 'DELETE' })

/** The same helpers, for a feature that keeps its calls in its own file (lib/autoMod.ts). */
export const http = { request, post, put, del }

export const api = {
  onboardingStatus: () => request<OnboardingStatus>('/api/onboarding/status'),

  demoStatus: () => request<DemoStatus>('/api/demo'),

  resetDemo: () => request<{ started: boolean }>('/api/demo/reset', { method: 'POST' }),

  /**
   * `setupCode` is the code printed to the server's console, needed while no account exists
   * (first-run setup code design). Once one does, the session is what counts and it is left out.
   */
  createAdministrator: (
    body: {
      username: string
      password: string
      confirmPassword: string
      email: string
      subscribeToUpdates?: boolean
    },
    setupCode?: string,
  ) =>
    request<CurrentUser>('/api/onboarding/administrator', {
      method: 'POST',
      body: JSON.stringify(body),
      headers: setupCode ? { 'x-setup-code': setupCode } : undefined,
    }),

  server: () => request<ServerInfo>('/api/server'),

  serverSettings: () => request<ServerSettings>('/api/settings/server'),

  setServerSettings: (showOwnerEmail: boolean) =>
    put<ServerSettings>('/api/settings/server', { showOwnerEmail }),

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
    discord?: {
      botToken?: string
      guildId?: string
      instanceChannelId?: string
      instanceMessage?: string
      instanceShowNames?: boolean
    }
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

  login: (body: { username: string; password: string; keepSignedIn: boolean }) =>
    post<CurrentUser>('/api/auth/login', body),

  logout: () => post<void>('/api/auth/logout'),

  me: () => request<CurrentUser>('/api/auth/me'),

  // ── The signed-in person's own account ──────────────────────────────────────────────────

  changePassword: (body: { currentPassword: string; newPassword: string; confirmPassword: string }) =>
    put<void>('/api/auth/password', body),

  changeUsername: (body: { username: string; currentPassword: string }) =>
    put<CurrentUser>('/api/auth/username', body),

  /** Null leaves the address alone. */
  setOwnContact: (body: { email?: string }) => put<CurrentUser>('/api/auth/contact', body),

  /** Where the browser goes to sign in to Discord and prove which account is yours. A page, not a fetch. */
  connectDiscordUrl: '/api/auth/discord/connect',

  disconnectDiscord: () => del<CurrentUser>('/api/auth/discord'),

  signOutEverywhere: () => post<void>('/api/auth/sign-out-everywhere'),

  /** Whether events that name you as host or staff invite you. */
  setEventInvites: (getsEventInvites: boolean) => put<CurrentUser>('/api/auth/event-invites', { getsEventInvites }),

  vrchatLink: () => request<VRChatLinkStatus>('/api/auth/vrchat-link'),

  startVRChatLink: (userIdOrUrl: string) =>
    post<VRChatLinkStatus>('/api/auth/vrchat-link/start', { userIdOrUrl }),

  checkVRChatLink: () => post<LinkCheckResult>('/api/auth/vrchat-link/check'),

  // ── Discord account linking: the member page ───────────────────────────────────────────

  linkPage: () => request<LinkPageStatus>('/api/discord-link'),

  /** A full page navigation, not a fetch: the browser goes to Discord and comes back to /link. */
  linkSignInUrl: '/api/discord-link/sign-in',

  linkVRChat: (userIdOrUrl: string) => post<LinkPageStatus>('/api/discord-link/vrchat', { userIdOrUrl }),

  linkCheck: () => post<LinkPageCheckResult>('/api/discord-link/check'),

  linkUnlink: () => post<LinkPageStatus>('/api/discord-link/unlink'),

  linkSignOut: () => post<void>('/api/discord-link/sign-out'),

  // ── Discord account linking: moderators ───────────────────────────────────────────────

  discordLinkFor: (vrchatUserId: string) =>
    request<{ link: DiscordLinkView | null }>(`/api/discord-links?vrchatUserId=${encodeURIComponent(vrchatUserId)}`),

  /** The same link, looked up from the Discord side. */
  discordLinkForDiscord: (discordUserId: string) =>
    request<{ link: DiscordLinkView | null }>(`/api/discord-links?discordUserId=${encodeURIComponent(discordUserId)}`),

  unlinkDiscord: (linkId: string) => post<void>(`/api/discord-links/${encodeURIComponent(linkId)}/unlink`),

  discordLinkingSettings: () => request<DiscordLinkingSettings>('/api/settings/discord-linking'),

  setDiscordLinkingSettings: (body: DiscordLinkingSettingsInput) =>
    put<DiscordLinkingSettings>('/api/settings/discord-linking', body),

  discordSync: () => request<DiscordSyncSettings>('/api/discord-sync'),

  setDiscordSync: (body: DiscordSyncSettingsInput) => put<DiscordSyncSettings>('/api/discord-sync', body),

  addRolePair: (body: RolePairInput) => post<DiscordSyncSettings>('/api/discord-sync/pairs', body),

  setRolePair: (id: string, body: RolePairInput) =>
    put<DiscordSyncSettings>(`/api/discord-sync/pairs/${encodeURIComponent(id)}`, body),

  deleteRolePair: (id: string) => del<DiscordSyncSettings>(`/api/discord-sync/pairs/${encodeURIComponent(id)}`),

  /** What the two syncs would change right now. Changes nothing. */
  previewDiscordSync: () => post<SyncPreview>('/api/discord-sync/preview'),

  /** Copies the roles and bans that are already different. */
  runDiscordSync: () => post<SyncPreview>('/api/discord-sync/run'),


  /** Reports what the deployment can do. Nothing about any account. */
  forgotPasswordWays: () => request<ForgotPasswordWays>('/api/auth/forgot-password'),

  /** Always the same sentence back, whoever asked. */
  forgotPassword: (username: string) =>
    post<{ message: string }>('/api/auth/forgot-password', { username }),

  // ── Invite and reset links, from the side of the person holding one ─────────────────────

  invite: (token: string) => request<InviteView>(`/api/join/${encodeURIComponent(token)}`),

  acceptInvite: (
    token: string,
    body: {
      username: string
      password: string
      confirmPassword: string
      email: string
      subscribeToUpdates?: boolean
    },
  ) => post<CurrentUser>(`/api/join/${encodeURIComponent(token)}`, body),

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
  }) => post<UserSummary>('/api/users', body),

  setUserRoles: (id: string, roleIds: string[]) => put<UserSummary>(`/api/users/${id}/roles`, { roleIds }),

  disableUser: (id: string) => post<UserSummary>(`/api/users/${id}/disable`),

  enableUser: (id: string) => post<UserSummary>(`/api/users/${id}/enable`),

  deleteUser: (id: string, username: string) =>
    post<UserSummary>(`/api/users/${id}/delete`, { username }),

  setUserContact: (id: string, body: { email?: string }) =>
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

  /** One place up or down. Answers with the roles in their new order. */
  moveRole: (id: string, direction: 'up' | 'down') =>
    post<RolesResponse>(`/api/roles/${id}/move`, { direction }),

  // ── Settings that the accounts layer added ──────────────────────────────────────────────

  publicAddress: () => request<PublicAddressView>('/api/settings/public-address'),

  setPublicAddress: (publicAddress: string) =>
    put<PublicAddressView>('/api/settings/public-address', { publicAddress }),

  publicInstances: () => request<PublicInstancesView>('/api/settings/public-instances'),

  setPublicInstances: (shared: boolean) => put<PublicInstancesView>('/api/settings/public-instances', { shared }),

  vrchatPictureUploads: () => request<VRChatPictureUploadsView>('/api/settings/vrchat-pictures'),

  setVRChatPictureUploads: (on: boolean) => put<VRChatPictureUploadsView>('/api/settings/vrchat-pictures', { on }),

  updateCheck: () => request<UpdateView>('/api/settings/updates'),

  setUpdateCheck: (on: boolean) => put<UpdateView>('/api/settings/updates', { on }),

  sendTestEmail: (to: string) => post<TestEmailResult>('/api/settings/email/test', { to }),

  emailSettings: () => request<EmailSettings>('/api/settings/email'),

  setEmailLimit: (limitPer24Hours: number) =>
    put<EmailSettings>('/api/settings/email/limit', { limitPer24Hours }),

  // ── AI ──────────────────────────────────────────────────────────────────────────────────

  aiSettings: () => request<AiSettings>('/api/settings/ai'),

  setAiSettings: (body: AiSettingsInput) => put<AiSettings>('/api/settings/ai', body),

  acknowledgeAi: (endpoint: string) => post<AiSettings>('/api/settings/ai/acknowledge', { endpoint }),

  /** One short chat message through the endpoint on the form. A 200 either way; `message` says what happened. */
  testAi: (body: AiConnectionInput) =>
    post<{ worked: boolean; message: string }>('/api/settings/ai/test', body),

  aiModels: (body: AiConnectionInput) =>
    post<{ models: string[]; error: string | null }>('/api/settings/ai/models', body),

  /** OpenRouter's models as last fetched, ordered for the feature the picker was opened from. */
  aiCatalog: (feature: AiModelFeature) =>
    request<AiCatalog>(`/api/settings/ai/catalog?feature=${feature}`),

  /** AI calls, newest first. Counts only; open one to see what the model was sent. */
  aiCalls: (filters: Partial<AiCallFilters> = {}, skip?: number) => {
    const query = new URLSearchParams()
    if (filters.feature) query.set('feature', filters.feature)
    if (filters.outcome) query.set('outcome', filters.outcome)
    if (filters.model) query.set('model', filters.model)
    if (filters.from) query.set('from', new Date(filters.from).toISOString())
    if (filters.to) query.set('to', new Date(filters.to).toISOString())
    if (filters.flagged) query.set('flagged', 'true')
    if (skip) query.set('skip', String(skip))

    const text = query.toString()
    return request<AiCallLogPage>(`/api/settings/ai/calls${text ? `?${text}` : ''}`)
  },

  aiCall: (id: string) => request<AiCallDetail>(`/api/settings/ai/calls/${id}`),

  aiInsightsSettings: () => request<AiInsightsSettings>('/api/settings/ai/insights'),

  setAiInsightsSettings: (body: AiInsightsSettingsInput) =>
    put<AiInsightsSettings>('/api/settings/ai/insights', body),

  /** Waits for the model. A 200 either way; `text` or `error` says which. 409 while AI is off. */
  generateInsight: (kind: InsightKind) => post<Insight>(`/api/settings/ai/insights/${kind}/generate`),

  /** Written insights only, newest first. */
  insights: (kind?: InsightKind, limit = 10) =>
    request<{ insights: Insight[] }>(`/api/insights?limit=${limit}${kind ? `&kind=${kind}` : ''}`),

  /** This account's notifications, newest first, plus the critical ones still waiting to be seen. */
  notifications: () =>
    request<{ waiting: ModbotNotification[]; recent: ModbotNotification[] }>('/api/notifications'),

  markNotificationSeen: (id: string) => post<void>(`/api/notifications/${id}/seen`),

  notificationChoices: () => request<{ channels: NotificationChoice[] }>('/api/notifications/choices'),

  setNotificationChoices: (channels: { channel: string; level: NotificationLevel; dailySummary: boolean }[]) =>
    put<{ channels: NotificationChoice[] }>('/api/notifications/choices', { channels }),

  aiAlertSettings: () => request<AiAlertSettings>('/api/settings/ai/alerts'),

  setAiAlertSettings: (body: AiAlertSettingsInput) => put<AiAlertSettings>('/api/settings/ai/alerts', body),

  /** Recent alerts nobody has hidden, newest first. `all` lists every alert kept. */
  alerts: (all = false, limit = 20) =>
    request<{ alerts: Alert[] }>(`/api/alerts?limit=${limit}${all ? '&all=true' : ''}`),

  dismissAlert: (id: string) => post<Alert>(`/api/alerts/${id}/dismiss`),

  aiChatSettings: () => request<AiChatSettings>('/api/settings/ai/chat'),

  aiLimits: () => request<AiLimits>('/api/settings/ai/limits'),

  /** Replaces every money limit, and every token limit when `tokenLimits` is given. */
  setAiLimits: (limits: AiLimitInput[], tokenLimits?: AiTokenLimitInput[]) =>
    put<AiLimits>('/api/settings/ai/limits', { limits, tokenLimits: tokenLimits ?? null }),

  /** Sets the default allowance and every allowance of a member's own. What is left out goes back to the default. */
  setAiAllowances: (body: AiAllowancesInput) => put<AiLimits>('/api/settings/ai/allowances', body),

  /** Replaces every entered price. Usage is priced when it is read, so this reprices earlier usage too. */
  setAiPrices: (prices: AiPrice[]) => put<AiLimits>('/api/settings/ai/prices', { prices }),

  /** Fetches OpenRouter's prices now. 502 with the reason when it fails. */
  fetchAiPrices: () => post<AiLimits>('/api/settings/ai/prices/fetch'),

  setAiChatSettings: (body: AiChatSettingsInput) => put<AiChatSettings>('/api/settings/ai/chat', body),

  // ── Chat ────────────────────────────────────────────────────────────────────────────────

  chatHome: () => request<ChatHome>('/api/chat'),

  chatConversation: (id: string) => request<ChatConversation>(`/api/chat/conversations/${encodeURIComponent(id)}`),

  deleteChatConversation: (id: string) => del<void>(`/api/chat/conversations/${encodeURIComponent(id)}`),

  renameChatConversation: (id: string, title: string) =>
    put<ChatConversationSummary>(`/api/chat/conversations/${encodeURIComponent(id)}/title`, { title }),

  /** Reads another version of a message, and the newest reply written after it. */
  readChatVersion: (id: string, messageId: number) =>
    post<ChatConversation>(`/api/chat/conversations/${encodeURIComponent(id)}/version`, { messageId }),

  chatConversationSpend: (id: string) =>
    request<AiSpent>(`/api/chat/conversations/${encodeURIComponent(id)}/spend`),

  sendChatMessage,

  // ── API keys, live events and webhooks (API keys design) ────────────────────────────────

  apiKeys: () => request<ApiKeysResponse>('/api/api-keys'),

  createApiKey: (body: { name: string; permissions: string[]; expiresAt: string | null }) =>
    post<CreatedApiKey>('/api/api-keys', body),

  revokeApiKey: (id: string) => del<void>(`/api/api-keys/${encodeURIComponent(id)}`),

  eventTypes: () => request<EventTypeOption[]>('/api/events/types'),

  /**
   * A one-use ticket for the event WebSocket. With `key`, the ticket stands for that key rather
   * than the signed-in session.
   */
  eventTicket: (key?: string) =>
    request<{ ticket: string; expiresAt: string }>('/api/events/tickets', {
      method: 'POST',
      headers: key ? { authorization: `Bearer ${key}` } : undefined,
    }),

  webhooks: () => request<WebhooksResponse>('/api/webhooks'),

  createWebhook: (body: WebhookInput) => post<CreatedWebhook>('/api/webhooks', body),

  updateWebhook: (id: string, body: WebhookInput) =>
    put<WebhookView>(`/api/webhooks/${encodeURIComponent(id)}`, body),

  deleteWebhook: (id: string) => del<void>(`/api/webhooks/${encodeURIComponent(id)}`),

  rollWebhookSecret: (id: string) =>
    post<{ secret: string }>(`/api/webhooks/${encodeURIComponent(id)}/secret`),

  testWebhook: (id: string) =>
    post<WebhookDeliveryView>(`/api/webhooks/${encodeURIComponent(id)}/test`),

  webhookDeliveries: (id: string) =>
    request<WebhookDeliveryView[]>(`/api/webhooks/${encodeURIComponent(id)}/deliveries`),

  setWebhookSettings: (body: { allowPrivateAddresses: boolean }) =>
    put<{ allowPrivateAddresses: boolean }>('/api/settings/webhooks', body),

  // ── Companion ──────────────────────────────────────────────────────────────────────

  /** Needs Pair a companion. A device token can never mint another device token. */
  issuePairingCode: () => post<IssuedPairingCode>('/api/companion-devices/pairing-code'),

  /** Your own companions; everybody's with Manage users. */
  pairedCompanions: () => request<PairedCompanion[]>('/api/companion-devices'),

  /** Your own, or anybody's with Manage users. The companion is refused from its next request. */
  removePairedCompanion: (id: string) => del<void>(`/api/companion-devices/${encodeURIComponent(id)}`),

  /**
   * Capacity is a what-if input answered against, never stored — nothing in Modbot behaves
   * differently for having been told, so the browser owns that state. A per-GB cost never leaves
   * the browser at all: the chart multiplies the sizes it already has.
   */
  dataSettings: (budget?: { capacityBytes?: number }) => {
    const q = new URLSearchParams()
    if (budget?.capacityBytes) q.set('capacityBytes', String(budget.capacityBytes))
    const query = q.toString()
    return request<DataSettings>(`/api/settings/data${query ? `?${query}` : ''}`)
  },

  setRetention: (body: {
    moderationFactRetentionDays: number
    presenceFactRetentionDays: number
    discordMessageRetentionDays: number
  }) => request<typeof body>('/api/settings/retention', {
    method: 'PUT',
    body: JSON.stringify(body),
  }),

  // ── Purge a person ─────────────────────────────────────────────────────────────────────────

  purgePreview: (platform: 'VRChat' | 'Discord', subjectId: string) => {
    const q = new URLSearchParams({ platform, subjectId })
    return request<PurgePreview>(`/api/settings/purge?${q.toString()}`)
  },

  /** Irreversible. `confirmation` is the id typed again and must match exactly. */
  purgePerson: (body: {
    platform: 'VRChat' | 'Discord'
    subjectId: string
    confirmation: string
  }) => post<PurgeReceipt>('/api/settings/purge', body),

  /** Read-only in this build: a control whose value is silently discarded is worse than no control. */
  syncSettings: () => request<SyncSettings>('/api/settings/sync'),

  // ── The VRChat proxy (VRChat proxy design) ─────────────────────────────────────────────────

  vrchatProxySettings: () => request<VRChatProxySettings>('/api/settings/vrchat-proxy'),

  setVRChatProxySettings: (body: { enabled: boolean; imagesProxied: boolean }) =>
    put<VRChatProxySettings>('/api/settings/vrchat-proxy', body),

  /**
   * One request through the proxy, with the signed-in session. Not `request`: a 404 from VRChat is
   * the answer the playground exists to show, not a failure. Only a refusal from Modbot itself --
   * the proxy off, no permission, its own pacing -- is thrown.
   */
  vrchatProxy: async (method: string, path: string, body?: string): Promise<VRChatProxyAnswer> => {
    let response: Response
    try {
      response = await fetch('/api/proxy/vrchat/' + path.replace(/^\/+/, ''), {
        method,
        headers: body ? { 'content-type': 'application/json' } : undefined,
        body: body || undefined,
      })
    } catch {
      throw new ApiError(0, 'Could not reach the Modbot server. Is it still running?', null)
    }

    const text = await response.text()
    const account = response.headers.get('x-modbot-proxy-account')

    if (account === null && (response.status === 401 || response.status === 403 || response.status === 404 || response.status === 429 || response.status === 503)) {
      let message = `The server answered ${response.status}.`
      try {
        const parsed: unknown = text ? JSON.parse(text) : null
        if (typeof parsed === 'object' && parsed !== null && 'error' in parsed) message = String((parsed as { error: unknown }).error)
      } catch {
        // Not JSON: the generic sentence stands.
      }
      throw new ApiError(response.status, message, null)
    }

    return { status: response.status, contentType: response.headers.get('content-type'), text, account }
  },

  /** The Discord server's channels as the bot last stored them. Use the pickers rather than calling this. */
  discordChannels: () => request<DiscordChannels>('/api/discord/channels'),

  discordRoles: () => request<DiscordRoles>('/api/discord/roles'),

  /** The channels events are sent to, with the event types, roles and names the editor needs. */
  discordRoutes: () => request<DiscordRoutes>('/api/discord/routes'),

  createDiscordRoute: (body: DiscordRouteBody) => post<DiscordRoute>('/api/discord/routes', body),

  /** Fields left out stay as they are. */
  updateDiscordRoute: (id: string, body: DiscordRouteBody) =>
    put<DiscordRoute>(`/api/discord/routes/${encodeURIComponent(id)}`, body),

  deleteDiscordRoute: (id: string) => del<void>(`/api/discord/routes/${encodeURIComponent(id)}`),

  /** Stored VRChat profiles by name or id, for a channel's filters. */
  /** This person's profile after each recorded change, newest first. Needs ViewProfile. */
  userHistory: (id: string) => request<ProfileHistory>(`/api/vrchat-users/history?id=${encodeURIComponent(id)}`),

  /** The bodies VRChat last sent for this person, as stored. Needs ViewProfile. */
  userRaw: (id: string) => request<RawProfile>(`/api/vrchat-users/raw?id=${encodeURIComponent(id)}`),

  /** People, Discord people and worlds by name or id, for the command palette. */
  search: (q: string, limit?: number) =>
    request<SearchResults>(`/api/search?q=${encodeURIComponent(q)}${limit ? `&limit=${limit}` : ''}`),

  discordRoutePeople: (search: string) =>
    request<{ people: DiscordRoutePerson[] }>(
      `/api/discord/routes/people?search=${encodeURIComponent(search)}`,
    ),

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
    query.notType?.forEach((t) => q.append('notType', t))
    query.source?.forEach((s) => q.append('source', s))
    if (query.subject) q.set('subject', query.subject)
    if (query.subjectPlatform) q.set('subjectPlatform', query.subjectPlatform)
    if (query.actor) q.set('actor', query.actor)
    if (query.actorPlatform) q.set('actorPlatform', query.actorPlatform)
    if (query.account) q.set('account', query.account)
    if (query.person) q.set('person', query.person)
    if (query.personPlatform) q.set('personPlatform', query.personPlatform)
    if (query.show) q.set('show', query.show)
    if (query.from) q.set('from', query.from)
    if (query.to) q.set('to', query.to)
    if (query.world) q.set('world', query.world)
    if (query.instance) q.set('instance', query.instance)
    if (query.category) q.set('category', query.category)
    if (query.precision) q.set('precision', query.precision)
    if (query.hasActor !== undefined) q.set('hasActor', String(query.hasActor))
    if (query.q) q.set('q', query.q)
    if (query.limit) q.set('limit', String(query.limit))
    if (query.before) {
      q.set('beforeOccurredAt', query.before.occurredAt)
      q.set('beforeId', String(query.before.id))
    }
    const search = q.toString()
    return request<AuditPage>(`/api/audit${search ? `?${search}` : ''}`)
  },

  auditFilters: () => request<AuditFilters>('/api/audit/filters'),

  /** One VRChat user's visits, newest first. `before` is the `next` of the page before. */
  personVisits: (userId: string, before: AuditCursor | null = null, limit = 10) => {
    const q = new URLSearchParams({ person: userId, limit: String(limit) })
    if (before) {
      q.set('beforeStartedAt', before.occurredAt)
      q.set('beforeId', String(before.id))
    }
    return request<PersonVisitsPage>(`/api/audit/visits?${q.toString()}`)
  },

  /**
   * One person's VRChat, Discord and Modbot accounts, from any one of them. Give exactly one.
   *
   * Ids go in the query string, never in the path: a VRChat id is arbitrary text (spec 3.1.1).
   */
  person: (ask: PersonAsk) => {
    const q = new URLSearchParams()
    if (ask.vrchat) q.set('vrchatUserId', ask.vrchat)
    if (ask.discord) q.set('discordUserId', ask.discord)
    if (ask.account) q.set('accountId', ask.account)
    return request<PersonView>(`/api/people/lookup?${q.toString()}`)
  },

  /** One entry by its id. 404 when it does not exist or this account may not read its type. */
  auditEntry: (id: string) => request<AuditEntry>(`/api/audit/entries/${encodeURIComponent(id)}`),

  auditAround: (id: number) => request<AuditAround>(`/api/audit/entries/${id}/around`),

  bans: (query: { offset?: number; limit?: number; includeUnbanned?: boolean } = {}) => {
    const q = new URLSearchParams()
    if (query.offset) q.set('offset', String(query.offset))
    if (query.limit) q.set('limit', String(query.limit))
    if (query.includeUnbanned === false) q.set('includeUnbanned', 'false')
    const search = q.toString()
    return request<BanList>(`/api/audit/bans${search ? `?${search}` : ''}`)
  },

  /**
   * Everyone Modbot has a record of, member or not. The member list is the group's roster; this
   * is the whole table behind it, and most of it is people who were never members.
   */
  people: (query: PeopleQuery = {}) => {
    const q = new URLSearchParams()
    if (query.search) q.set('search', query.search)
    if (query.membership && query.membership !== 'all') q.set('membership', query.membership)
    if (query.banned !== undefined) q.set('banned', String(query.banned))
    if (query.everBanned !== undefined) q.set('everBanned', String(query.everBanned))
    if (query.profile) q.set('profile', query.profile)
    if (query.eighteenPlus !== undefined) q.set('eighteenPlus', String(query.eighteenPlus))
    query.trustRanks?.forEach((r) => q.append('trustRank', r))
    query.platforms?.forEach((p) => q.append('platform', p))
    if (query.linked && query.linked !== 'all') q.set('linked', query.linked)
    if (query.flagged !== undefined) q.set('flagged', String(query.flagged))
    if (query.seenFrom) q.set('seenFrom', query.seenFrom)
    if (query.seenTo) q.set('seenTo', query.seenTo)
    query.roles?.forEach((r) => q.append('role', r))
    query.notRoles?.forEach((r) => q.append('notRole', r))
    if (query.hasRole !== undefined) q.set('noRole', String(!query.hasRole))
    if (query.representing !== undefined) q.set('representing', String(query.representing))
    if (query.joinedFrom) q.set('joinedFrom', query.joinedFrom)
    if (query.joinedTo) q.set('joinedTo', query.joinedTo)
    if (query.sort && query.sort !== 'seen') q.set('sort', query.sort)
    if (query.page && query.page > 1) q.set('page', String(query.page))
    if (query.pageSize) q.set('pageSize', String(query.pageSize))
    const search = q.toString()
    return request<PeopleList>(`/api/people${search ? `?${search}` : ''}`)
  },

  // The Discord server's members: a list of its own, because most people are on one side only.
  discordMembers: (query: DiscordMemberQuery = {}) => {
    const q = new URLSearchParams()
    if (query.search) q.set('search', query.search)
    if (query.state && query.state !== 'in-server') q.set('state', query.state)
    query.roles?.forEach((r) => q.append('role', r))
    query.notRoles?.forEach((r) => q.append('notRole', r))
    if (query.hasRole !== undefined) q.set('noRole', String(!query.hasRole))
    if (query.linked && query.linked !== 'all') q.set('linked', query.linked)
    if (query.bot !== undefined) q.set('bot', String(query.bot))
    if (query.pending !== undefined) q.set('pending', String(query.pending))
    if (query.timedOut !== undefined) q.set('timedOut', String(query.timedOut))
    if (query.boosting !== undefined) q.set('boosting', String(query.boosting))
    if (query.joinedFrom) q.set('joinedFrom', query.joinedFrom)
    if (query.joinedTo) q.set('joinedTo', query.joinedTo)
    if (query.sort && query.sort !== 'joined') q.set('sort', query.sort)
    if (query.page && query.page > 1) q.set('page', String(query.page))
    if (query.pageSize) q.set('pageSize', String(query.pageSize))
    const search = q.toString()
    return request<DiscordMemberList>(`/api/discord/members${search ? `?${search}` : ''}`)
  },

  discordMember: (id: string) => request<DiscordMember>(`/api/discord/members/${encodeURIComponent(id)}`),

  /** The server itself, for the header over the member list: the Discord page's own header. */
  discordServer: () => request<ServerProfile>('/api/discord/server'),

  /** The Discord page's Roles tab. */
  discordRoleReport: () => request<RoleReport>('/api/discord/reports/roles'),

  /** The Discord page's Channels tab, without the channels @everyone cannot see when `hideStaffOnly`. */
  discordQuietChannels: (hideStaffOnly: boolean) =>
    request<QuietChannelList>(`/api/discord/reports/quiet-channels${hideStaffOnly ? '?hideStaffOnly=true' : ''}`),

  /** `at` is a message id to open on: the page holding it comes back, whatever `page` says. */
  discordMemberMessages: (id: string, page: number, pageSize: number, at?: string) =>
    request<DiscordMemberMessages>(
      `/api/discord/members/${encodeURIComponent(id)}/messages?page=${page}&pageSize=${pageSize}` +
        (at ? `&at=${encodeURIComponent(at)}` : ''),
    ),

  discordMemberMetrics: (id: string) =>
    request<DiscordMemberMetrics>(`/api/discord/members/${encodeURIComponent(id)}/metrics`),

  /** One person's membership and ban standing. The id goes in the query string (spec 3.1.1). */
  membership: (id: string) =>
    request<MembershipView>(`/api/members/membership?id=${encodeURIComponent(id)}`),

  /** The group's ban list as last swept. Distinct from `bans`, which is what the audit log recorded. */
  groupBans: (query: GroupBanQuery = {}) => {
    const q = new URLSearchParams()
    if (query.search) q.set('search', query.search)
    if (query.status && query.status !== 'current') q.set('status', query.status)
    if (query.caseFile && query.caseFile !== 'any') q.set('caseFile', query.caseFile)
    if (query.page && query.page > 1) q.set('page', String(query.page))
    if (query.pageSize) q.set('pageSize', String(query.pageSize))
    const search = q.toString()
    return request<GroupBanList>(`/api/bans${search ? `?${search}` : ''}`)
  },

  /** The Discord server's ban list, as the bot last read it and kept it since. */
  discordBans: (query: GroupBanQuery = {}) => {
    const q = new URLSearchParams()
    if (query.search) q.set('search', query.search)
    if (query.status && query.status !== 'current') q.set('status', query.status)
    if (query.page && query.page > 1) q.set('page', String(query.page))
    if (query.pageSize) q.set('pageSize', String(query.pageSize))
    const search = q.toString()
    return request<DiscordBanList>(`/api/discord/bans${search ? `?${search}` : ''}`)
  },

  /**
   * The Analytics section, one page per question (spec 10.1). `query` is `days=30` or `all=true`,
   * built by the pages' shared range control so every page means the same thing by a range.
   */
  groupAnalytics: (query: string) => request<GroupAnalytics>(`/api/analytics/group?${query}`),
  groupMemberCount: (range: MemberCountRange) =>
    request<GroupMemberCountSeries>(`/api/analytics/group/member-count?range=${range}`),
  groupInfo: () => request<GroupInfo>('/api/analytics/group/info'),
  // The group's own page on VRChat. Each is one request to VRChat, made because somebody pressed
  // something; nothing here is called on a timer.
  updateGroupProfile: (edit: GroupProfileEdit) =>
    request<GroupInfo>('/api/group/profile', { method: 'PUT', body: JSON.stringify(edit) }),
  groupPosts: (page = 1) => request<GroupPostList>(`/api/group/posts?page=${page}`),
  createGroupPost: (body: GroupPostBody) =>
    request<{ post: GroupPostRow }>('/api/group/posts', { method: 'POST', body: JSON.stringify(body) }),
  updateGroupPost: (body: GroupPostBody) =>
    request<{ post: GroupPostRow }>('/api/group/posts', { method: 'PUT', body: JSON.stringify(body) }),
  deleteGroupPost: (id: string, title: string | null) =>
    request<void>('/api/group/posts/delete', { method: 'POST', body: JSON.stringify({ id, title }) }),
  groupRoles: () => request<GroupRoleList>('/api/group/roles'),
  createGroupRole: (body: GroupRoleBody) =>
    request<{ role: GroupRoleRow }>('/api/group/roles', { method: 'POST', body: JSON.stringify(body) }),
  updateGroupRole: (body: GroupRoleBody) =>
    request<{ role: GroupRoleRow }>('/api/group/roles', { method: 'PUT', body: JSON.stringify(body) }),
  deleteGroupRole: (id: string, name: string | null) =>
    request<void>('/api/group/roles/delete', { method: 'POST', body: JSON.stringify({ id, name }) }),
  groupInvites: (page = 1) => request<GroupInviteList>(`/api/group/invites?page=${page}`),
  cancelGroupInvite: (userId: string, displayName: string | null) =>
    request<void>('/api/group/invites/cancel', { method: 'POST', body: JSON.stringify({ userId, displayName }) }),
  groupGallery: (galleryId: string | null, page = 1) =>
    request<GroupGalleryPage>(
      `/api/group/gallery?page=${page}${galleryId ? `&galleryId=${encodeURIComponent(galleryId)}` : ''}`,
    ),
  removeGroupGalleryImage: (galleryId: string, imageId: string, submittedById: string | null) =>
    request<void>('/api/group/gallery/remove', {
      method: 'POST',
      body: JSON.stringify({ galleryId, imageId, submittedById }),
    }),
  teamAnalytics: (query: string) => request<TeamAnalytics>(`/api/analytics/team?${query}`),
  /** Saves how many people in an instance want a moderator in it. Needs Change settings. */
  setCoverPeople: (people: number) =>
    request<{ people: number }>('/api/analytics/team/people', { method: 'PUT', body: JSON.stringify({ people }) }),
  worldsAnalytics: (query: string) => request<WorldsAnalytics>(`/api/analytics/worlds?${query}`),
  instancesAnalytics: (query: string) => request<InstancesAnalytics>(`/api/analytics/instances?${query}`),
  instanceActivity: (range: MemberCountRange) =>
    request<InstanceActivitySeries>(`/api/analytics/instances/activity?range=${range}`),
  serverAnalytics: (query: string) => request<ServerAnalytics>(`/api/analytics/server?${query}`),

  // One world and one instance, for the popup. Read from Modbot's own tables; neither costs VRChat
  // budget, so a popup may be opened as often as a moderator likes.
  world: (id: string) => request<WorldView>(`/api/worlds?id=${encodeURIComponent(id)}`),

  instance: (id: string) => request<InstanceView>(`/api/instances/${encodeURIComponent(id)}`),

  /** An AI brief of what was recorded in one instance. A paid call each time; see `Brief`. */
  instanceBrief: (id: string, timeZone: string) =>
    post<Brief>(`/api/briefs/instances/${encodeURIComponent(id)}`, { timeZone }),

  /** An AI brief of what was recorded about one person, from any one of their accounts. */
  personBrief: (ask: PersonAsk, timeZone: string) =>
    post<Brief>('/api/briefs/people', {
      vrchatUserId: ask.vrchat,
      discordUserId: ask.discord,
      accountId: ask.account,
      timeZone,
    }),
  instanceWorld: (id: string) => request<InstanceWorldView>(`/api/instances/${encodeURIComponent(id)}/world`),

  live: () => request<LiveView>('/api/live'),

  /** Flagged people in the group's open instances now: the number beside Live. Needs ViewLiveInstances. */
  liveFlaggedCount: () => request<{ here: number }>('/api/live/flagged-count'),

  /** Tells the server the Now page is on screen, and reads what happened since the last look. */
  lookAtNow: () => post<NowLook>('/api/now/look'),

  /** A one-use ticket for the live updates WebSocket (live updates design §4). */
  liveTicket: () => request<{ ticket: string; expiresAt: string }>('/api/live/tickets', { method: 'POST' }),

  /** Live updates by long polling, the backup for the WebSocket: events after `after`, waiting up to `waitSeconds`. */
  livePoll: (after: string | null, waitSeconds: number) =>
    request<{ events: unknown[]; cursor: string; more: boolean }>(
      `/api/live/poll?wait=${waitSeconds}${after ? `&after=${encodeURIComponent(after)}` : ''}`,
    ),

  userMetrics: (id: string) =>
    request<PersonMetrics>(`/api/vrchat-users/metrics?id=${encodeURIComponent(id)}`),

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
  }) => request<typeof body>('/api/settings/evidence/limits', {
    method: 'PUT',
    body: JSON.stringify(body),
  }),

  probeEvidenceStore: () => post<EvidenceHealth>('/api/settings/evidence/probe'),

  gateHealth: () => request<GateHealth>('/api/health/gate'),

  syncHealth: () => request<SyncHealth>('/api/health/sync'),

  discordBotState: () => request<DiscordBotStateView>('/api/health/discord-bot'),

  machineUsage: () => request<MachineUsage>('/api/health/machine'),

  logs: (query: LogQuery = {}) => {
    const q = new URLSearchParams()
    if (query.level) q.set('level', query.level)
    if (query.source) q.set('source', query.source)
    if (query.area) q.set('area', query.area)
    if (query.text) q.set('text', query.text)
    if (query.from) q.set('from', query.from)
    if (query.to) q.set('to', query.to)
    if (query.before) q.set('before', String(query.before))
    if (query.limit) q.set('limit', String(query.limit))
    const search = q.toString()
    return request<LogPage>(`/api/logs${search ? `?${search}` : ''}`)
  },

  logFilters: () => request<LogFilters>('/api/logs/filters'),

  creditsShowcase: () => request<Showcase>('/api/credits/showcase'),

  healthAlerts: () => request<HealthAlertView>('/api/health/alerts'),

  setHealthAlerts: (body: {
    quietHours: number
    storageWarnGb: number
    checksOn: string[]
    recipientUserIds: string[]
  }) => put<HealthAlertView>('/api/health/alerts', body),

  logSettings: () => request<LogSettings>('/api/logs/settings'),

  setLogSettings: (body: { keepDays: number; sendToCloud: boolean }) =>
    put<LogSettings>('/api/logs/settings', body),

  /**
   * Whether Modbot can reach its database, from the readiness probe a hosting platform calls.
   *
   * Not `request`: a Modbot that cannot reach its database answers 503, and that is an answer
   * rather than a failure -- the process is up and is telling us what is wrong. Only a request
   * that never completed throws, and the caller shows that as unknown.
   */
  databaseHealth: async (): Promise<boolean> => (await fetch('/health/ready')).ok,
  cloudStatus: () => request<CloudStatusView>('/api/settings/cloud'),
  setCloudReport: (reportOn: boolean) => put<CloudStatusView>('/api/settings/cloud', { reportOn }),
  cloudLinkCode: () => post<LinkCodeView>('/api/settings/cloud/link-code', {}),

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

  // ── Reviews and repeat offenders (spec 5.8) ─────────────────────────────────────────────

  /** Reviews of a moderator's pattern. Needs ReviewTickets. */
  reviews: (state: 'open' | 'closed' | 'all' = 'open') => request<ReviewList>(`/api/reviews?state=${state}`),

  openReviewCount: () => request<{ open: number }>('/api/reviews/open-count'),

  /** The note is required: it is kept with the review and recorded as a fact against your account. */
  closeReview: (id: string, note: string, outcome?: 'right' | 'wrong') =>
    post<ReviewView>(`/api/reviews/${encodeURIComponent(id)}/close`, { note, outcome }),

  /** People acted on more than once, most recent action first. Needs ViewProfile. */
  repeatOffenders: (query: { status?: 'all' | 'repeat' | 'more-than-once'; offset?: number; limit?: number } = {}) => {
    const q = new URLSearchParams()
    if (query.status && query.status !== 'all') q.set('status', query.status)
    if (query.offset) q.set('offset', String(query.offset))
    if (query.limit) q.set('limit', String(query.limit))
    const search = q.toString()
    return request<RepeatOffenderList>(`/api/repeat-offenders${search ? `?${search}` : ''}`)
  },

  repeatOffenderRules: () => request<RepeatOffenderRules>('/api/settings/repeat-offenders'),

  /** Rebuilds every person's counts before it answers, because both are rules they are computed under. */
  setRepeatOffenderRules: (threshold: number, types: string[]) =>
    put<RepeatOffenderRules>('/api/settings/repeat-offenders', { threshold, types }),

  flagRules: () => request<FlagRules>('/api/settings/flag-rules'),

  setFlagRules: (body: FlagRulesInput) => put<FlagRules>('/api/settings/flag-rules', body),

  autoInvites: () => request<AutoInvites>('/api/settings/auto-invites'),

  /** Refuses fewer than the minimum minutes, and refuses a rule tree the server cannot read. */
  setAutoInvites: (body: AutoInvitesInput) => put<AutoInvites>('/api/settings/auto-invites', body),

  /** One person's history block. The id goes in the query string, never the path (spec 3.1.1). */
  subjectHistory: (id: string) =>
    request<SubjectHistory>(`/api/repeat-offenders/one?id=${encodeURIComponent(id)}`),

  /** Set or clear the sticky 18+ flag by hand. Needs the EditAgeVerification permission. */
  setAgeVerified: (id: string, body: { verified: boolean; reason?: string }) =>
    request<VRChatUserProfile>(`/api/vrchat-users/age-verified?id=${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  // ── Ban case files (spec 5.8.3) ─────────────────────────────────────────────────────────

  /** The reason buttons. Anyone signed in may read; `canEdit` says whether this person may change them. */
  banReasons: () => request<BanReasonList>('/api/settings/ban-reasons'),

  createBanReason: (body: { label: string; description: string; needsWrittenReason: boolean; usedFor: ReasonUseName[] }) =>
    post<BanReasonView>('/api/settings/ban-reasons', body),

  updateBanReason: (
    id: string,
    body: { label: string; description: string; needsWrittenReason: boolean; isActive: boolean; usedFor: ReasonUseName[] },
  ) => put<BanReasonView>(`/api/settings/ban-reasons/${encodeURIComponent(id)}`, body),

  /** Whether a kick, an unban and a rejection need a reason too. Needs EditClassifications. */
  setReasonAlwaysRequired: (required: boolean) =>
    put<{ required: boolean }>('/api/settings/ban-reasons/required', { required }),

  reorderBanReasons: (ids: string[]) => put<BanReasonList>('/api/settings/ban-reasons/order', { ids }),

  /** Case files, newest first. `userId` narrows to one person; the id goes in the query (spec 3.1.1). */
  cases: (query: { userId?: string; includeWithdrawn?: boolean; offset?: number; limit?: number } = {}) => {
    const q = new URLSearchParams()
    if (query.userId) q.set('userId', query.userId)
    if (query.includeWithdrawn) q.set('includeWithdrawn', 'true')
    if (query.offset) q.set('offset', String(query.offset))
    if (query.limit) q.set('limit', String(query.limit))
    const search = q.toString()
    return request<CaseFileList>(`/api/cases${search ? `?${search}` : ''}`)
  },

  /** Bans in the last `days` with no case file, newest first. Needs ViewProfile. */
  unwrittenCases: (days?: number, limit?: number) => {
    const q = new URLSearchParams()
    if (days) q.set('days', String(days))
    if (limit) q.set('limit', String(limit))
    const search = q.toString()
    return request<UnwrittenBanList>(`/api/cases/missing${search ? `?${search}` : ''}`)
  },

  /** Whether each of these people has a case file -- the badge on a ban list. */
  caseLookup: (userIds: string[]) => {
    const q = new URLSearchParams()
    userIds.forEach((id) => q.append('userId', id))
    return request<CaseFileLookup[]>(`/api/cases/lookup?${q.toString()}`)
  },

  caseFile: (id: string) => request<CaseFileView>(`/api/cases/${encodeURIComponent(id)}`),

  /** Writes the case file. A 409 carries `detail.caseId`, the one that already exists for this ban. */
  createCaseFile: (body: {
    userId: string
    auditEntryId?: string | null
    reasonIds: string[]
    writtenReason: string
  }) => post<CaseFileCreated>('/api/cases', body),

  updateCaseFile: (id: string, body: { reasonIds: string[]; writtenReason: string }) =>
    put<CaseFileView>(`/api/cases/${encodeURIComponent(id)}`, body),

  withdrawCaseFile: (id: string, note: string) =>
    post<CaseFileView>(`/api/cases/${encodeURIComponent(id)}/withdraw`, { note }),

  captureCaseFileAgain: (id: string) => post<CaseFileView>(`/api/cases/${encodeURIComponent(id)}/capture-again`),

  // ── Evidence uploads (evidence design §9.1). Phase 2, the bytes, is an XMLHttpRequest in the
  //    gallery so it can report progress; it is not here. ────────────────────────────────────

  /** `reportId` is null when the case file is not written yet; the commit names it then. */
  beginEvidenceUpload: (body: { fileName: string; contentType: string; length: number; reportId: string | null }) =>
    post<EvidenceUploadTicket>('/api/evidence/uploads', body),

  /** `clipId` says the file is that saved clip; the server refuses it unless the bytes match its fingerprint. */
  commitEvidenceUpload: (
    uploadId: string,
    expectedHash: string | null,
    reportId: string | null = null,
    clipId: number | null = null,
  ) =>
    post<EvidenceCommitted>(`/api/evidence/uploads/${encodeURIComponent(uploadId)}/commit`, {
      expectedHash,
      reportId,
      clipId,
    }),

  /** Upload limits for a screen with no case file yet — the ban dialog. */
  evidenceDelivery: () => request<EvidenceDelivery>('/api/cases/evidence-delivery'),

  /**
   * Where the bytes of a piece of evidence are served from. Same-origin, authenticated by the cookie.
   *
   * `caseId` says which case file's page is asking, so the audit log names it. `view` is for a page
   * showing the file: it is written to the log as "Evidence viewed", at most once per person per
   * file every ten minutes. Without it the request is a download and is written every time, so a
   * Download link must not pass it.
   */
  evidenceUrl: (hash: string, options?: { caseId?: string; view?: boolean }) => {
    const query = new URLSearchParams()
    if (options?.caseId) query.set('case', options.caseId)
    if (options?.view) query.set('view', 'true')
    const text = query.toString()
    return `/api/evidence/${encodeURIComponent(hash)}${text ? `?${text}` : ''}`
  },

  /** Ends this case file's hold on the file. The bytes and every other case file's hold stay. */
  takeEvidenceOff: (caseId: string, hash: string) =>
    post<{ caseId: string; hash: string }>(
      `/api/cases/${encodeURIComponent(caseId)}/evidence/${encodeURIComponent(hash)}/take-off`,
    ),

  /** Destroys the bytes, from a case file. There is no undo. */
  destroyEvidence: (hash: string, caseId: string, reason: string) =>
    post<EvidenceDestroyed>(`/api/evidence/${encodeURIComponent(hash)}/destroy`, { reason, caseId }),

  // ── Moderation actions (M4 §4). The only calls that change anything in VRChat. ────────────

  kickPerson: (body: ModerationActionBody) => post<ModerationActionResult>('/api/moderation/kick', body),

  banPerson: (body: ModerationActionBody) => post<ModerationActionResult>('/api/moderation/ban', body),

  unbanPerson: (body: ModerationActionBody) => post<ModerationActionResult>('/api/moderation/unban', body),

  // ── Notes (notes design). Modbot's own record about a person; nothing reaches VRChat. ─────

  /** One person's notes, newest first. Needs ViewAuditLog — a note is a fact in that log. */
  notes: (query: { userId: string; platform?: string; limit?: number }) => {
    const q = new URLSearchParams({ userId: query.userId })
    if (query.platform) q.set('platform', query.platform)
    if (query.limit) q.set('limit', String(query.limit))
    return request<NoteList>(`/api/notes?${q.toString()}`)
  },

  /** Write a note about somebody. Needs WriteNotes. */
  writeNote: (body: { userId: string; platform?: string; text: string }) => post<Note>('/api/notes', body),

  /**
   * Save one of your person briefs as a note about that person, marked as written by AI. The
   * server writes the note's words from the brief; 409 once it is saved. Needs WriteNotes.
   */
  saveBriefAsNote: (callId: string) => post<Note>(`/api/briefs/${encodeURIComponent(callId)}/note`),

  /** Take a note back. Nothing is deleted; a second fact records that it no longer stands. */
  takeBackNote: (id: number) => post<Note>(`/api/notes/${id}/take-back`),

  // ── Watching a person (watching a person design). Modbot's own record; nothing reaches VRChat. ─

  /** The watches on a person's accounts, the standing one first. Needs ViewAuditLog. */
  personWatches: (ids: { vrchat?: string | null; discord?: string | null }) => {
    const q = new URLSearchParams()
    if (ids.vrchat) q.set('vrchat', ids.vrchat)
    if (ids.discord) q.set('discord', ids.discord)
    return request<PersonWatches>(`/api/watches/person?${q.toString()}`)
  },

  /** Every standing watch, or with `due` only those whose follow-up day has come. Needs ViewAuditLog. */
  watches: (query: { due?: boolean } = {}) => request<WatchList>(`/api/watches${query.due ? '?due=true' : ''}`),

  /** Start watching somebody. Needs WriteNotes. */
  startWatch: (body: StartWatchBody) => post<Watch>('/api/watches', body),

  stopWatch: (id: string) => post<Watch>(`/api/watches/${id}/stop`),

  /** Somebody has checked on the person: clears the follow-up day. */
  followedUp: (id: string) => post<Watch>(`/api/watches/${id}/followed-up`),
  // ── Join requests (join requests design). Read live from VRChat every time. ───────────────

  joinRequests: (query: JoinRequestQuery = {}) => {
    const q = new URLSearchParams()
    if (query.page && query.page > 1) q.set('page', String(query.page))
    if (query.pageSize) q.set('pageSize', String(query.pageSize))
    const search = q.toString()
    return request<JoinRequestList>(`/api/requests${search ? `?${search}` : ''}`)
  },

  approveJoinRequest: (body: JoinRequestAnswerBody) =>
    post<ModerationActionResult>('/api/requests/approve', body),

  rejectJoinRequest: (body: JoinRequestAnswerBody) =>
    post<ModerationActionResult>('/api/requests/reject', body),

  // ── The MCP server (MCP server design) ───────────────────────────────────────────────────

  mcpSettings: () => request<McpSettings>('/api/mcp/settings'),

  setMcpSettings: (body: { enabled: boolean }) => put<McpSettings>('/api/mcp/settings', body),

  mcpConnections: () => request<{ connections: McpConnection[] }>('/api/mcp/connections'),

  disconnectMcp: (id: string) => del<void>(`/api/mcp/connections/${encodeURIComponent(id)}`),

  /** `search` is the query string the AI app sent the browser with, `?` included. */
  mcpSignIn: (search: string) => request<McpSignInView>(`/api/mcp/authorize${search}`),

  answerMcpSignIn: (body: McpSignInAnswer) => post<{ redirectTo: string }>('/api/mcp/authorize', body),
}
