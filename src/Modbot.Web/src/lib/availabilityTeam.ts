// What the team's heatmap shows from the people the server sent: who is shown after the filters,
// how many of them are free in each hour, the shade of each hour, and the best stretches of each
// day. Nothing that talks to the server or the page, so the Node test runner loads it as it is.
//
// The hours here are the VIEWER's: every person's week has already been moved into the viewer's
// zone (`inViewersWeek` in availabilityZones.ts), so a person is a list of 168 states, one for each
// hour of the viewer's week, day by day from Monday.
import { DAY_NAMES, hourText, type AvailabilityCell, type AvailabilityState } from './availabilityZones.ts'

/** One person on the team, as the server sends them. */
export type TeamPerson = {
  id: string
  name: string
  roles: string[]
  /** The zone their cells are in. Null until they have saved a week. */
  timeZone: string | null
  cells: AvailabilityCell[]
}

export type DaysShown = 'every' | 'weekdays' | 'weekend'

export type HoursShown = 'all' | 'evening' | 'daytime' | 'night'

export type TeamFilters = {
  /** One role's name, or null for every role. */
  role: string | null
  /** The people to show, by id; none picked means everybody. */
  people: string[]
  days: DaysShown
  hours: HoursShown
  /** The fewest people who must be free for an hour to stand out and to be a best time. 1 is any. */
  least: 1 | 2 | 3 | 4
  /** Whether somebody who is free if needed counts as free. */
  ifNeeded: boolean
}

export const NO_FILTERS: TeamFilters = { role: null, people: [], days: 'every', hours: 'all', least: 1, ifNeeded: true }

/** The hours of the day each choice shows, by the viewer's clock. Night, daytime and evening share none. */
export const HOUR_RANGES: Record<HoursShown, readonly [number, number]> = {
  all: [0, 23],
  night: [0, 5],
  daytime: [6, 17],
  evening: [18, 23],
}

/** The days each choice shows, 0 for Monday. */
export const DAY_RANGES: Record<DaysShown, readonly [number, number]> = {
  every: [0, 6],
  weekdays: [0, 4],
  weekend: [5, 6],
}

function span(range: readonly [number, number]): number[] {
  const [from, to] = range
  return Array.from({ length: to - from + 1 }, (_, i) => from + i)
}

export function shownDays(filters: TeamFilters): number[] {
  return span(DAY_RANGES[filters.days])
}

export function shownHours(filters: TeamFilters): number[] {
  return span(HOUR_RANGES[filters.hours])
}

/** The names of the roles anybody on the team holds, sorted. */
export function rolesOf(people: readonly TeamPerson[]): string[] {
  return [...new Set(people.flatMap((p) => p.roles))].sort((a, b) => a.localeCompare(b))
}

/** The people the role and person filters leave. */
export function shownPeople(people: readonly TeamPerson[], filters: TeamFilters): TeamPerson[] {
  return people.filter(
    (p) =>
      (filters.role === null || p.roles.includes(filters.role)) &&
      (filters.people.length === 0 || filters.people.includes(p.id)),
  )
}

/** Who is free in one hour of the viewer's week. */
export type Slot = {
  /** How many are free, counting those free if needed only when the filters say so. */
  count: number
  people: { id: string; name: string; state: AvailabilityState }[]
}

/**
 * Every hour of the viewer's week, day by day from Monday: who among `people` is free in it.
 * `weeks` holds each person's week in the viewer's grid, by id; someone with none is free never.
 */
export function tally(
  people: readonly TeamPerson[],
  weeks: ReadonlyMap<string, readonly (AvailabilityState | null)[]>,
  ifNeeded: boolean,
): Slot[] {
  return Array.from({ length: 7 * 24 }, (_, at) => {
    const free: Slot['people'] = []
    for (const person of people) {
      const state = weeks.get(person.id)?.[at] ?? null
      if (state === 'free' || (state === 'ifNeeded' && ifNeeded)) free.push({ id: person.id, name: person.name, state })
    }
    return { count: free.length, people: free }
  })
}

/**
 * How dark an hour is drawn, 0 for none to 4 for most: the share of the people shown who are free,
 * in four steps. An hour with fewer than `least` free is drawn as none, so the minimum filter
 * fades what is not enough.
 */
export function shadeOf(count: number, shown: number, least: number): 0 | 1 | 2 | 3 | 4 {
  if (count === 0 || shown === 0 || count < least) return 0
  return Math.min(4, Math.max(1, Math.ceil((count * 4) / shown))) as 1 | 2 | 3 | 4
}

/** A stretch of hours of one day. `to` is the hour it ends at, so 18 to 22 is 18:00-22:00. */
export type BestTime = { day: number; from: number; to: number; count: number }

/**
 * The best stretch of each day: the longest run of hours in a row, among those with the most people
 * free that day, the earliest first. A day where nobody, or fewer than `least`, is free has none.
 * The days come best first, then longest, then in the order of the week.
 */
export function bestTimes(slots: readonly Slot[], days: readonly number[], hours: readonly number[], least: number): BestTime[] {
  const found: BestTime[] = []

  for (const day of days) {
    let most = 0
    for (const hour of hours) most = Math.max(most, slots[day * 24 + hour]?.count ?? 0)
    if (most === 0 || most < least) continue

    let best: BestTime | null = null
    let run: BestTime | null = null

    for (const hour of hours) {
      const count = slots[day * 24 + hour]?.count ?? 0
      if (count !== most) {
        run = null
        continue
      }

      // Hours are shown in order and one after another, so the run goes on while they do.
      if (run && run.to === hour) run.to = hour + 1
      else run = { day, from: hour, to: hour + 1, count }

      if (!best || run.to - run.from > best.to - best.from) best = run
    }

    if (best) found.push({ ...best })
  }

  return found.sort((a, b) => b.count - a.count || b.to - b.from - (a.to - a.from) || a.day - b.day)
}

/** `Tue 18:00-22:00 · 5 of 8 free`. */
export function bestTimeText(time: BestTime, shown: number): string {
  return `${DAY_NAMES[time.day]} ${hourText(time.from)}-${hourText(time.to % 24)} · ${time.count} of ${shown} free`
}
