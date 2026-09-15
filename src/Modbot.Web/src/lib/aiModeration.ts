import { http } from '@/lib/api'

/** Where a piece of text came from. The server's names. */
export type ModerationTarget = 'discordMessage' | 'displayName' | 'bio' | 'status' | 'pronouns'

export const TARGETS: { value: ModerationTarget; label: string }[] = [
  { value: 'discordMessage', label: 'Discord messages' },
  { value: 'displayName', label: 'Display name' },
  { value: 'bio', label: 'Bio' },
  { value: 'status', label: 'Status' },
  { value: 'pronouns', label: 'Pronouns' },
]

export type RuleStats = { flags: number; dismissed: number }

/** Where a rule runs and who it never acts on. */
export type RuleScope = {
  channelMode: 'all' | 'only' | 'except'
  channels: string[]
  exemptRoles: string[]
  exemptRolesSkipFlag: boolean
}

export const NO_SCOPE: RuleScope = {
  channelMode: 'all',
  channels: [],
  exemptRoles: [],
  exemptRolesSkipFlag: false,
}

export type RuleTrial = {
  startedAt: string
  days: number
  endsAt: string
  flags: number
  wouldDelete: number
  wouldTimeOut: number
  dismissed: number
}

export type RulePause = { at: string; reason: string | null }

export type RuleTestSummary = {
  samples: number
  lastRunAt: string | null
  lastRunModel: string | null
  caught: number | null
  shouldFlag: number | null
  wronglyFlagged: number | null
  passes: boolean
}

export type RuleSafety = {
  version: number
  acting: boolean
  scope: RuleScope
  trial: RuleTrial | null
  paused: RulePause | null
  tests: RuleTestSummary
}

export type HubChanges = { added: number; removed: number; changed: number }

export type TermListView = {
  id: string
  name: string
  source: 'local' | 'cloud'
  enabled: boolean
  targets: ModerationTarget[]
  deleteMessage: boolean
  timeoutMinutes: number | null
  setToActBy: string | null
  setToActAt: string | null
  termCount: number
  excludedCount: number
  hubId: string | null
  hubVersion: string | null
  hubFetchedAt: string | null
  hubAvailableVersion: string | null
  hubAvailableChanges: HubChanges | null
  hubError: string | null
  stats: RuleStats
} & RuleSafety

export type TermKind = 'word' | 'contains' | 'regex' | 'combination'

export type TermView = {
  id: string
  kind: TermKind
  text: string | null
  pattern: string | null
  label: string
  category: string | null
  note: string | null
  excluded: boolean
}

export type TermListDetail = { list: TermListView; terms: TermView[] }

export type Sensitivity = 'low' | 'medium' | 'high'

export type TopicView = {
  id: string
  name: string
  instructions: string
  sensitivity: Sensitivity
  enabled: boolean
  targets: ModerationTarget[]
  deleteMessage: boolean
  timeoutMinutes: number | null
  setToActBy: string | null
  setToActAt: string | null
  stats: RuleStats
} & RuleSafety

export type AiModeration = {
  enabled: boolean
  dailyAiCallLimit: number
  aiCallsToday: number
  aiReady: boolean
  lists: TermListView[]
  topics: TopicView[]
}

export type TermInput = {
  id: string | null
  kind: Exclude<TermKind, 'combination'>
  text: string
}

export type RuleAction = {
  enabled: boolean
  targets: ModerationTarget[]
  deleteMessage: boolean
  timeoutMinutes: number | null
  scope: RuleScope
  trialDays: number | null
  actWithoutTest?: boolean
}

export type TermListInput = RuleAction & {
  name: string
  terms?: TermInput[]
  excludedTerms?: string[]
}

export type TopicInput = RuleAction & {
  name: string
  instructions: string
  sensitivity: Sensitivity
}

export type HubListView = {
  id: string
  name: string
  description: string | null
  version: string | null
  ruleCount: number
  suitableFor: string[]
  subscribed: boolean
}

export type TryMatch = {
  ruleKind: 'termList' | 'topic'
  ruleId: string
  ruleName: string
  ruleEnabled: boolean
  term: string
  matched: string
  reason: string | null
  deleteMessage: boolean
  timeoutMinutes: number | null
}

export type TryResult = {
  matches: TryMatch[]
  wouldDeleteMessage: boolean
  wouldTimeOutMinutes: number | null
  aiSkipped: string | null
}

export type TestSample = {
  id: string
  text: string
  shouldFlag: boolean
  note: string | null
  target: ModerationTarget
  seeded: boolean
}

export type TestSampleInput = {
  text: string
  shouldFlag: boolean
  note: string | null
  target: ModerationTarget
}

export type TestRunSample = {
  sampleId: string
  text: string
  shouldFlag: boolean
  note: string | null
  target: ModerationTarget
  flagged: boolean
  term: string | null
  matched: string | null
  reason: string | null
}

export type TestRun = {
  id: string
  ranAt: string
  model: string | null
  ruleVersion: number
  samples: number
  shouldFlagCount: number
  caught: number
  missed: number
  shouldNotFlagCount: number
  wronglyFlagged: number
  aiSkipped: string | null
  ranBy: string | null
  results: TestRunSample[]
}

export type RuleTests = {
  ruleKind: RuleKind
  ruleId: string
  ruleName: string
  ruleVersion: number
  acting: boolean
  samples: TestSample[]
  runs: TestRun[]
}

export type RuleVersion = {
  version: number
  changedAt: string
  changedBy: string | null
  name: string
  text: string
}

export type RuleKind = 'termList' | 'topic'

export type ModerationFlag = {
  id: string
  flaggedAt: string
  ruleKind: 'termList' | 'topic'
  ruleId: string
  ruleName: string
  term: string
  target: ModerationTarget
  subjectPlatform: 'vrchat' | 'discord'
  subjectId: string
  subjectName: string | null
  channelId: string | null
  messageId: string | null
  matched: string
  reason: string | null
  messageDeleted: boolean
  timedOutMinutes: number | null
  state: 'open' | 'dismissed'
  dismissedAt: string | null
  dismissedBy: string | null
  ruleVersion: number
  ruleText: string | null
  trial: boolean
  wouldDeleteMessage: boolean
  wouldTimeOutMinutes: number | null
}

const base = '/api/settings/ai/moderation'

export const moderationApi = {
  settings: () => http.request<AiModeration>(base),

  saveSettings: (body: { enabled: boolean; dailyAiCallLimit: number }) => http.put<AiModeration>(base, body),

  list: (id: string) => http.request<TermListDetail>(`${base}/lists/${id}`),

  createList: (body: TermListInput) => http.post<TermListDetail>(`${base}/lists`, body),

  updateList: (id: string, body: TermListInput) => http.put<TermListDetail>(`${base}/lists/${id}`, body),

  deleteList: (id: string) => http.del<void>(`${base}/lists/${id}`),

  hub: () => http.request<{ lists: HubListView[]; error: string | null }>(`${base}/hub`),

  addHubList: (hubId: string) => http.post<TermListDetail>(`${base}/lists/hub`, { hubId }),

  refreshHubList: (id: string) => http.post<TermListDetail>(`${base}/lists/${id}/refresh`),

  applyHubUpdate: (id: string) => http.post<TermListDetail>(`${base}/lists/${id}/update`),

  createTopic: (body: TopicInput) => http.post<TopicView>(`${base}/topics`, body),

  updateTopic: (id: string, body: TopicInput) => http.put<TopicView>(`${base}/topics/${id}`, body),

  deleteTopic: (id: string) => http.del<void>(`${base}/topics/${id}`),

  tryText: (body: { text: string; target: ModerationTarget; includeAi: boolean }) =>
    http.post<TryResult>(`${base}/try`, body),

  tests: (kind: RuleKind, id: string) => http.request<RuleTests>(`${base}/rules/${kind}/${id}/tests`),

  addSample: (kind: RuleKind, id: string, body: TestSampleInput) =>
    http.post<TestSample>(`${base}/rules/${kind}/${id}/samples`, body),

  updateSample: (kind: RuleKind, id: string, sampleId: string, body: TestSampleInput) =>
    http.put<TestSample>(`${base}/rules/${kind}/${id}/samples/${sampleId}`, body),

  deleteSample: (kind: RuleKind, id: string, sampleId: string) =>
    http.del<void>(`${base}/rules/${kind}/${id}/samples/${sampleId}`),

  runTests: (kind: RuleKind, id: string) => http.post<TestRun>(`${base}/rules/${kind}/${id}/tests/run`),

  versions: (kind: RuleKind, id: string) =>
    http.request<{ ruleKind: RuleKind; ruleId: string; versions: RuleVersion[] }>(
      `${base}/rules/${kind}/${id}/versions`,
    ),

  endTrial: (kind: RuleKind, id: string) => http.post<unknown>(`${base}/rules/${kind}/${id}/end-trial`),

  resume: (kind: RuleKind, id: string) => http.post<unknown>(`${base}/rules/${kind}/${id}/resume`),

  flags: (state: 'open' | 'dismissed') =>
    http.request<{ flags: ModerationFlag[]; open: number }>(`/api/moderation-flags?state=${state}`),

  dismissFlag: (id: string) => http.post<ModerationFlag>(`/api/moderation-flags/${id}/dismiss`),
}

/** "Flag only", "Delete", "Time out 60 min", "Delete, time out 60 min". */
export function actionLabel(rule: { deleteMessage: boolean; timeoutMinutes: number | null }): string {
  const parts = [
    rule.deleteMessage ? 'Delete' : null,
    rule.timeoutMinutes ? `${rule.deleteMessage ? 'time' : 'Time'} out ${rule.timeoutMinutes} min` : null,
  ].filter(Boolean)
  return parts.length ? parts.join(', ') : 'Flag only'
}

export function targetLabel(target: ModerationTarget): string {
  return TARGETS.find((t) => t.value === target)?.label ?? target
}

/** "12 flags · 58% dismissed", or "No flags". */
export function statsLabel(stats: RuleStats): string {
  if (stats.flags === 0) return 'No flags'
  const percent = Math.round((stats.dismissed / stats.flags) * 100)
  return `${stats.flags} ${stats.flags === 1 ? 'flag' : 'flags'} · ${percent}% dismissed`
}

/** "9 of 10 caught · 1 wrongly flagged", or "No test run". */
export function testsLabel(tests: RuleTestSummary): string {
  if (!tests.lastRunAt) return tests.samples > 0 ? `${tests.samples} samples · no run` : 'No test set'
  return `${tests.caught ?? 0} of ${tests.shouldFlag ?? 0} caught · ${tests.wronglyFlagged ?? 0} wrongly flagged`
}

/** "Only 2 channels", "All but 1 channel", "2 roles exempt", joined. Null when the rule has no limits. */
export function scopeLabel(scope: RuleScope): string | null {
  const parts = [
    scope.channelMode === 'only' ? `Only ${count(scope.channels.length, 'channel')}` : null,
    scope.channelMode === 'except' ? `All but ${count(scope.channels.length, 'channel')}` : null,
    scope.exemptRoles.length > 0 ? `${count(scope.exemptRoles.length, 'role')} exempt` : null,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : null
}

/** "3 would be deleted · 1 would be timed out · 2 dismissed". */
export function trialLabel(trial: RuleTrial): string {
  return [
    `${count(trial.wouldDelete, 'delete')}`,
    `${count(trial.wouldTimeOut, 'timeout')}`,
    `${trial.dismissed} dismissed`,
  ].join(' · ')
}

function count(n: number, word: string): string {
  return `${n} ${n === 1 ? word : `${word}s`}`
}

/** What the server said when something failed, in its words. */
export function failure(e: unknown, fallback: string): string {
  if (typeof e === 'object' && e !== null && 'status' in e && (e as { status: number }).status === 403)
    return 'You do not have permission to change AI settings.'
  return e instanceof Error && e.message ? e.message : fallback
}
