import { http } from '@/lib/api'

/** Where a giveaway is (giveaways design §3.1). */
export type GiveawayState = 'draft' | 'open' | 'closed' | 'drawn' | 'cancelled'

export type GiveawayEntryWay = 'automatic' | 'react'

export type GiveawayPostState = 'waiting' | 'published' | 'failed' | 'removed'

/** One rule, or a group of rules combined. The same shape the server stores. */
export type GiveawayRule = {
  kind: string
  rules?: GiveawayRule[]
  amount?: number
  withinDays?: number | null
  id?: string
}

export type GiveawayExclusions = {
  staff: boolean
  pastWinners: boolean
  bannedMembers: boolean
  people: string[]
}

export type GiveawayPost = {
  state: GiveawayPostState
  channelId: string | null
  error: string | null
  errorAt: string | null
  updatedAt: string
}

export type GiveawayEntrant = {
  position: number
  key: string
  vrChatUserId: string | null
  discordUserId: string | null
  name: string | null
  weight: number
  measured: number
  keptOut: string
  keptOutLabel: string
  because: string | null
  fromPolledData: boolean
  closeCall: boolean
  winnerRank: number | null
  purged: boolean
}

export type GiveawayDraw = {
  id: string
  number: number
  drawnAt: string
  drawnBy: string | null
  seed: string
  seedPromise: string
  seedKept: boolean
  winnerCount: number
  ruleLines: string[]
  exclusions: string[]
  weighting: string
  weightCap: number | null
  entrantCount: number
  inDrawCount: number
  totalWeight: number
  fromPolledData: boolean
  closeCalls: number
  winners: GiveawayEntrant[]
}

export type Giveaway = {
  id: string
  name: string
  prize: string
  opensAt: string
  closesAt: string
  drawAt: string | null
  winnerCount: number
  entryWay: GiveawayEntryWay
  emoji: string
  rules: GiveawayRule
  ruleLines: string[]
  exclusions: GiveawayExclusions
  exclusionLines: string[]
  weighting: string
  weightCap: number | null
  postToChannel: boolean
  channelId: string | null
  state: GiveawayState
  seedPromise: string
  drawCount: number
  entryCount: number
  version: number
  createdAt: string
  updatedAt: string
  openedAt: string | null
  closedAt: string | null
  post: GiveawayPost | null
  draws: GiveawayDraw[]
}

export type GiveawayList = { giveaways: Giveaway[]; canRun: boolean; now: string }

export type GiveawayPreview = {
  total: number
  inDraw: number
  totalWeight: number
  closeCalls: number
  fromPolledData: boolean
  unanswerable: string | null
  stopped: boolean
  people: GiveawayEntrant[]
}

export type GiveawayEntrants = {
  people: GiveawayEntrant[]
  total: number
  page: number
  pageSize: number
}

export type GiveawayRole = { id: string; name: string }

export type GiveawayBuilder = {
  ruleKinds: string[]
  weightings: string[]
  groupRoles: GiveawayRole[]
  discordRoles: GiveawayRole[]
  moderationFactRetentionDays: number
  presenceFactRetentionDays: number
}

export type GiveawayInput = {
  name: string
  prize: string
  opensAt: string
  closesAt: string
  drawAt: string | null
  winnerCount: number
  entryWay: GiveawayEntryWay
  emoji: string
  rules: GiveawayRule
  exclusions: GiveawayExclusions
  weighting: string
  weightCap: number | null
  postToChannel: boolean
  channelId: string | null
  draft: boolean
}

const base = '/api/giveaways'

export const giveawayApi = {
  list: () => http.request<GiveawayList>(base),
  one: (id: string) => http.request<Giveaway>(`${base}/${encodeURIComponent(id)}`),
  builder: () => http.request<GiveawayBuilder>(`${base}/builder`),
  create: (body: GiveawayInput) => http.post<Giveaway>(base, body),
  update: (id: string, body: GiveawayInput) => http.put<Giveaway>(`${base}/${id}`, body),
  open: (id: string) => http.post<void>(`${base}/${id}/open`),
  close: (id: string) => http.post<void>(`${base}/${id}/close`),
  draw: (id: string) => http.post<Giveaway>(`${base}/${id}/draw`),
  cancel: (id: string) => http.post<void>(`${base}/${id}/cancel`),
  remove: (id: string) => http.del<void>(`${base}/${id}`),
  preview: (body: {
    rules: GiveawayRule
    exclusions: GiveawayExclusions
    weighting: string
    weightCap: number | null
  }) => http.post<GiveawayPreview>(`${base}/preview`, body),
  entrants: (id: string, drawId: string, page: number, pageSize = 50) =>
    http.request<GiveawayEntrants>(
      `${base}/${id}/draws/${drawId}/entrants?page=${page}&pageSize=${pageSize}`,
    ),
}

export {
  COMBINE_LABEL,
  COMBINING,
  ENTRY_WAY_LABEL,
  POST_STATE_LABEL,
  RULE_LABEL,
  RULE_UNIT,
  STATE_LABEL,
  WEIGHTING_LABEL,
  blankGiveaway,
  fromLocalInput,
  fromPolledData,
  inputFrom,
  isCombining,
  localInputValue,
  measured,
  takesAmount,
  takesRole,
  takesWindow,
  toLocalInput,
} from './giveawayRules.ts'
