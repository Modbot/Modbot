import type { AuditEntry } from './api.ts'
import { dayHeading, localDayKey } from './format.ts'

/**
 * How a list of facts is laid out: under a heading per day, with one row per thing that happened.
 *
 * A clock time on its own answers "when?" wrongly as soon as a list crosses midnight, and the day
 * behind a hover title is out of reach on a phone or a VR laser pointer. So the day is a heading.
 *
 * And one thing that happened is one row, even when two sources each recorded it. A join VRChat's
 * audit log states and the member list sync notices is the same join; two rows for it read as the
 * person joining twice. Nothing is dropped: the row carries every source's badge, and a list asked
 * for one source only never has a partner to merge with, so it shows that source's row as it is.
 */

/** One row: the fact whose time and sentence are shown, and the same fact as the other sources recorded it. */
export type FactRow = { entry: AuditEntry; also: AuditEntry[] }

/** The rows of one of the viewer's days, newest first as they came. */
export type FactDay = { key: string; heading: string; rows: FactRow[] }

/**
 * How far apart two sources' times for the same fact may be.
 *
 * The same ten seconds the server links one decision's facts within (`LinkedActions.Window`): far
 * enough apart for two systems' clocks and rounding, and nowhere near long enough for somebody to
 * join, leave and join again.
 */
export const SAME_FACT_WITHIN_MS = 10_000

/**
 * Which source's copy of a fact is shown, best first.
 *
 * VRChat's own audit log states who did it and when. A sync only infers that something changed, so
 * its copy is shown last; the rest record what they saw themselves.
 */
const SOURCE_ORDER: Record<string, number> = {
  AuditLog: 0,
  Discord: 1,
  Modbot: 1,
  Client: 2,
  Manual: 3,
  Import: 3,
  SyncDiff: 4,
}

const orderOf = (entry: AuditEntry) => SOURCE_ORDER[entry.source] ?? 3

/** Both say the same, or one of them says nothing. A sync never knows who did it. */
const agree = (a: string | null, b: string | null) => a === null || b === null || a === b

/**
 * Whether two entries are one thing that happened, seen by two sources.
 *
 * Only facts with an exact time: a sync that knows only "between these two polls" cannot say two
 * things happened at the same moment, which is the server's rule for linking facts too.
 */
export function sameFact(a: AuditEntry, b: AuditEntry): boolean {
  return (
    a.source !== b.source &&
    a.type === b.type &&
    a.subjectPlatform === b.subjectPlatform &&
    a.subjectId === b.subjectId &&
    a.occurredBefore === null &&
    b.occurredBefore === null &&
    Math.abs(Date.parse(a.occurredAt) - Date.parse(b.occurredAt)) <= SAME_FACT_WITHIN_MS &&
    agree(a.actorPlatform, b.actorPlatform) &&
    agree(a.actorId, b.actorId) &&
    agree(a.worldId, b.worldId) &&
    agree(a.instanceId, b.instanceId)
  )
}

/**
 * The entries as rows, each thing that happened once, in the order given.
 *
 * A row takes at most one entry from each source, because two entries from one source are two
 * things that happened. An entry is in one row only. The row sits where its newest entry was.
 */
export function mergeSameFacts(entries: AuditEntry[]): FactRow[] {
  const used = new Set<number>()
  const rows: FactRow[] = []

  entries.forEach((entry, i) => {
    if (used.has(i)) return
    used.add(i)

    const group = [entry]
    for (let j = i + 1; j < entries.length; j++) {
      const other = entries[j]
      if (used.has(j) || !group.every((member) => sameFact(member, other))) continue
      used.add(j)
      group.push(other)
    }

    // Stable, so two sources of the same standing keep the order they came in.
    const [shown, ...also] = [...group].sort((a, b) => orderOf(a) - orderOf(b))
    rows.push({ entry: shown, also })
  })

  return rows
}

/**
 * The rows under a heading per day, against the server's `now`.
 *
 * A day is a run of rows in a row, not every row that falls on it, so the list keeps the order it
 * was given; a list sorted newest first gets one heading per day.
 */
export function groupByDay(rows: FactRow[], now: string): FactDay[] {
  const days: FactDay[] = []

  for (const row of rows) {
    const key = localDayKey(row.entry.occurredAt)
    const last = days[days.length - 1]

    if (last?.key === key) last.rows.push(row)
    else days.push({ key, heading: dayHeading(row.entry.occurredAt, now), rows: [row] })
  }

  return days
}

/** A list of facts laid out for reading: merged, then under their days. */
export const factDays = (entries: AuditEntry[], now: string): FactDay[] => groupByDay(mergeSameFacts(entries), now)

/**
 * Rows that are one run: the same kind of thing, about the same person, in the same minute.
 *
 * A person joining a Discord server is given a handful of roles in the same second, and the list
 * read as nine near-identical rows. A group is shown as one row that opens to the events inside.
 * `id` is the oldest event's id, which stays put when newer events arrive at the top of the list;
 * `rows` are in the order given, so the first is the one whose sentence stands for the group.
 */
export type FactGroup = { id: number; rows: FactRow[] }

/**
 * What puts rows in one group, or null for a row that is never grouped.
 *
 * Only people: a group is "the same thing done again to the same person", and for an instance, a
 * role or a group the same words can be different things. Only exact times: a sync that knows
 * "between these two polls" cannot say two things happened in the same minute. Who did it is left
 * out on purpose, because a sync never knows, so a run mixing sources still joins. The minute is
 * the viewer's own, as the list shows it.
 */
function groupKey({ entry }: FactRow): string | null {
  if (entry.subjectKind !== 'Person' || entry.precision !== 'Exact' || entry.occurredBefore !== null) return null

  const minute = new Date(entry.occurredAt).setSeconds(0, 0)
  return [entry.type, entry.subjectPlatform, entry.subjectId, minute].join('\u0000')
}

/**
 * The rows with each run of next-door rows that share a {@link groupKey} made into one group.
 *
 * Next to each other, not "anywhere in the same minute": the list is a timeline, and something
 * else that happened in between is part of the story. A row with nothing to join is a group of one.
 */
export function groupRuns(rows: FactRow[]): FactGroup[] {
  const groups: FactGroup[] = []
  let previous: string | null = null

  for (const row of rows) {
    const key = groupKey(row)
    const last = groups[groups.length - 1]

    if (last && key !== null && key === previous) {
      last.rows.push(row)
      last.id = row.entry.id
    } else {
      groups.push({ id: row.entry.id, rows: [row] })
    }
    previous = key
  }

  return groups
}

/** The sources that recorded a row's fact: the one shown first, then the others. */
export const rowSources = ({ entry, also }: FactRow): string[] => [entry, ...also].map((seen) => seen.source)

/** The sources a group's rows were recorded by, each once, in the order they first appear. */
export function groupSources(group: FactGroup): string[] {
  const seen: string[] = []
  for (const row of group.rows)
    for (const source of rowSources(row)) if (!seen.includes(source)) seen.push(source)
  return seen
}
