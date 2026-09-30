import type { InstanceView, PeoplePresentPoint } from '@/lib/api'
import { seriesColor } from '../components/charts/theme.ts'
import { trustRankColour, trustRankLabel, type TrustRank } from './trustRank.ts'

/**
 * The rows behind the instance popup's "People over time" chart. Plain functions, so the tests can
 * run them under node and the popup keeps its fast-refresh boundary.
 *
 * The chart is a staircase of the head count, coloured by what each step was: green going up,
 * dark red down at a kick, light red down when somebody left. Recharts draws one line in one
 * colour, so the total is drawn as four lines -- `up`, `kick`, `left` and `held` -- each holding a
 * value only along the steps of its own colour. A step runs from one reading to the next, and is
 * coloured by the reading it ends in, so each step puts its start under its colour's key at the
 * earlier reading and its end at the later one. Where two steps of different colours meet, the
 * reading is in the chart twice: once ending the first step, once starting the next
 * (`order` keeps them apart at one instant). A stretch that ended in no change -- the tail after
 * the last reading, or a reading that only changed source -- is `held`.
 *
 * The member and rank lines come from what a moderator's companion saw, at their own moments.
 * Every row carries every line's value as it stood at that instant, so the tooltip can say what
 * each line was whether or not it is drawn; a line's value is null before its first point and
 * after its last, where the line does not exist.
 */

export type ChangeKind = 'up' | 'kick' | 'left'

/** The keys the head count line is drawn under: one per colour, plus `held` for no change. */
export type StepKind = ChangeKind | 'held'
export const STEP_KINDS: readonly StepKind[] = ['up', 'kick', 'left', 'held']

export const RANK_KEYS = [
  'visitor',
  'newUser',
  'user',
  'knownUser',
  'trustedUser',
  'legend',
  'nuisance',
  'vrChatTeam',
  'rankUnknown',
] as const
export type RankKey = (typeof RANK_KEYS)[number]

/** The lines a moderator can turn on: members, then the ranks. */
export type PresenceKey = 'members' | RankKey
export const PRESENCE_KEYS: readonly PresenceKey[] = ['members', ...RANK_KEYS]

/** The ranks with a toggle whenever the lines exist. The others appear only when somebody held them. */
export const RANKS_ALWAYS: readonly RankKey[] = ['visitor', 'newUser', 'user', 'knownUser', 'trustedUser']

const RANK_OF: Record<RankKey, TrustRank | null> = {
  visitor: 'Visitor',
  newUser: 'NewUser',
  user: 'User',
  knownUser: 'KnownUser',
  trustedUser: 'TrustedUser',
  legend: 'Legend',
  nuisance: 'Nuisance',
  vrChatTeam: 'VRChatTeam',
  rankUnknown: null,
}

export function presenceLabel(key: PresenceKey): string {
  if (key === 'members') return 'Members'
  const rank = RANK_OF[key]
  return rank ? trustRankLabel(rank) : 'Rank not read'
}

/**
 * The colour a line is drawn in. Ranks take the colour VRChat paints them, the one their badge
 * already carries, except Visitor, whose VRChat grey vanishes on a light card and so takes the
 * theme's own grey. Members and "rank not read" take series slots no rank uses.
 */
export function presenceColour(key: PresenceKey): string {
  if (key === 'members') return seriesColor(1)
  if (key === 'rankUnknown') return seriesColor(5)
  if (key === 'visitor') return 'var(--muted-foreground)'
  return trustRankColour(RANK_OF[key]!)
}

export function stepColour(kind: StepKind | null): string {
  switch (kind) {
    case 'up':
      return 'var(--chart-up)'
    case 'kick':
      return 'var(--chart-kick)'
    case 'left':
      return 'var(--chart-left)'
    default:
      return 'var(--muted-foreground)'
  }
}

export const STEP_LABEL: Record<StepKind, string> = {
  up: 'Up',
  kick: 'Kicked',
  left: 'Left',
  held: 'No change',
}

export type PeopleRow = {
  at: number
  /** Sorts rows at one instant: a step's end, then the next step's start, then a presence change. */
  order: 0 | 1 | 2
  /** The head count as it stood at this instant; null before the first reading. */
  people: number | null
  unsure: boolean
  /** What the latest reading at or before this instant was against the one before it. */
  change: ChangeKind | null
} & Record<StepKind, number | null> &
  Record<PresenceKey, number | null>

type Reading = { at: number; people: number; unsure: boolean; change: ChangeKind | null }
type Presence = { at: number } & Record<PresenceKey, number>

function blank(at: number, order: 0 | 1 | 2): PeopleRow {
  return {
    at,
    order,
    people: null,
    unsure: false,
    change: null,
    up: null,
    kick: null,
    left: null,
    held: null,
    members: null,
    visitor: null,
    newUser: null,
    user: null,
    knownUser: null,
    trustedUser: null,
    legend: null,
    nuisance: null,
    vrChatTeam: null,
    rankUnknown: null,
  }
}

/**
 * The chart's rows, oldest first. Empty when there are no head counts: the lines that need a
 * companion are drawn over the head count, never instead of it.
 *
 * `to` is the end of the chart -- when the instance closed, or now -- and the last reading is
 * held to it, because it held until then.
 */
export function peopleOverTimeRows(
  headCounts: InstanceView['headCounts'],
  present: PeoplePresentPoint[],
  to: number,
): PeopleRow[] {
  const readings: Reading[] = headCounts
    .map((h) => ({ at: Date.parse(h.at), people: h.people, unsure: h.unsure, change: h.change }))
    .filter((r) => Number.isFinite(r.at))
    .sort((a, b) => a.at - b.at)

  if (readings.length === 0) return []

  const seen: Presence[] = present
    .map((p) => ({ ...p, at: Date.parse(p.at) }))
    .filter((p) => Number.isFinite(p.at))
    .sort((a, b) => a.at - b.at)

  const rows: PeopleRow[] = []

  const step = (fromAt: number, fromValue: number, toAt: number, toValue: number, kind: StepKind) => {
    const start = blank(fromAt, 1)
    start[kind] = fromValue
    const end = blank(toAt, 0)
    end[kind] = toValue
    rows.push(start, end)
  }

  for (let i = 1; i < readings.length; i++) {
    const before = readings[i - 1]
    const r = readings[i]
    step(before.at, before.people, r.at, r.people, r.change ?? 'held')
  }

  const last = readings[readings.length - 1]
  if (last.at < to) step(last.at, last.people, to, last.people, 'held')
  else if (readings.length === 1) rows.push(blank(last.at, 0))

  for (const p of seen) {
    const row = blank(p.at, 2)
    for (const key of PRESENCE_KEYS) row[key] = p[key]
    rows.push(row)
  }

  rows.sort((a, b) => a.at - b.at || a.order - b.order)

  // What each line stood at, at each instant: the latest reading or presence point at or before it.
  // Looked up rather than carried along the walk, so every row at one instant says the same thing
  // whichever of them the tooltip lands on.
  const lastPresenceAt = seen.length > 0 ? seen[seen.length - 1].at : Number.NEGATIVE_INFINITY
  for (const row of rows) {
    const reading = latestAtOrBefore(readings, row.at)
    if (reading) {
      row.people = reading.people
      row.unsure = reading.unsure
      row.change = reading.change
    }

    if (row.at <= lastPresenceAt) {
      const p = latestAtOrBefore(seen, row.at)
      if (p) for (const key of PRESENCE_KEYS) row[key] = p[key]
    }
  }

  // A presence change inside a step sits on that step's line, or the line would break there.
  let open: { kind: StepKind; value: number } | null = null
  for (const row of rows) {
    if (row.order === 0) open = null
    else if (row.order === 1) {
      const kind = STEP_KINDS.find((k) => row[k] !== null)
      open = kind ? { kind, value: row[kind]! } : null
    } else if (open) row[open.kind] = open.value
  }

  return rows
}

function latestAtOrBefore<T extends { at: number }>(sorted: T[], at: number): T | null {
  let lo = 0
  let hi = sorted.length - 1
  let found: T | null = null
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (sorted[mid].at <= at) {
      found = sorted[mid]
      lo = mid + 1
    } else hi = mid - 1
  }
  return found
}

/**
 * The lines to offer toggles for: members and the everyday ranks always, and the rarer ranks
 * (Legend, Nuisance, VRChat Team, and a rank not read yet) only when somebody there held one.
 */
export function presenceLines(present: PeoplePresentPoint[]): PresenceKey[] {
  const held = new Set<RankKey>(RANKS_ALWAYS)
  for (const p of present) for (const key of RANK_KEYS) if (p[key] > 0) held.add(key)
  return ['members', ...RANK_KEYS.filter((k) => held.has(k))]
}
