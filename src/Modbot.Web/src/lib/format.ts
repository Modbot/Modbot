/**
 * Formatting and series preparation.
 *
 * Plain functions, deliberately not in a component file: the fast-refresh boundary only works when
 * a module exports components or values, not both, and these are shared by every screen.
 */

const SOURCE_LABEL: Record<string, string> = {
  AuditLog: 'VRChat',
  SyncDiff: 'Sync',
  Companion: 'Companion App',
  // Called Client until 2026-09-24. Rows are unchanged; only the name is.
  Client: 'Companion App',
  Discord: 'Discord',
  Manual: 'Manual',
  Modbot: 'Modbot',
  // Legacy: imported records carry the source they really came from now (import design §5.1).
  // Kept because rows written before that still hold it.
  Import: 'Import',
}

/**
 * The name of the system a fact came from.
 *
 * `AuditLog` reads as "VRChat" because that is whose record it is, and `Client` reads as
 * "Companion App" because that is what the thing is called — spec 5.9.5's chips are named for the
 * sources people think in, not for the enum members. The stored values are unchanged; a row
 * written years ago still says `Client` in the database and always will.
 */
export const sourceLabel = (source: string): string => SOURCE_LABEL[source] ?? source

/**
 * Whether a date needs its year written: only when it is not in the viewer's current year.
 *
 * "Sep 26, 2026" on every row of a table in 2026 is a column of the same four digits, and it
 * pushes the part that differs off a phone. A date from another year still says so, because
 * "Mar 3" read in September means this March. Several dates shown as one thing -- a range, a
 * "between this and that" -- are passed together and all get the year if any needs it, so a range
 * over New Year reads "Dec 15, 2025 – Jan 10, 2026" rather than dropping the one it shares with now.
 *
 * The browser's clock, unlike `ago`: which year the viewer is in is a question about their own
 * calendar, and a wrong clock costs no more than a year printed or left out.
 */
export function needsYear(...isos: string[]): boolean {
  const thisYear = new Date().getFullYear()
  return isos.some((iso) => new Date(iso).getFullYear() !== thisYear)
}

/** A day, "Sep 26", with the year only when it is not this year ({@link needsYear}). */
export function formatDay(iso: string, withYear: boolean = needsYear(iso)): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: withYear ? 'numeric' : undefined,
    month: 'short',
    day: 'numeric',
  })
}

/** Two days as one range, "Aug 28 – Sep 26": both with the year, or neither. */
export function formatDayRange(from: string, to: string): string {
  const withYear = needsYear(from, to)
  return `${formatDay(from, withYear)} – ${formatDay(to, withYear)}`
}

/**
 * The noun that goes with a count: `plural(1, 'action')` is "action", `plural(2, 'action')` is
 * "actions". `many` for a word that does not just take an s -- `plural(n, 'person', 'people')`.
 * Only exactly one is singular; "0 actions" and "1.5 hours" are plural, as they are said.
 */
export function plural(n: number, one: string, many: string = `${one}s`): string {
  return n === 1 ? one : many
}

/**
 * How often a person has been acted on, and by how many moderators: "4 actions by 3 moderators".
 * The moderators are left off when nobody is named for any of the actions. "Moderators" in full,
 * not "mods", which is chat slang (site review 2026-09-27, finding 4).
 */
export function pastActions(actions: number, moderators: number): string {
  const done = `${actions} ${plural(actions, 'action')}`
  return moderators > 0 ? `${done} by ${moderators} ${plural(moderators, 'moderator')}` : done
}

/** A place in an order, the way people say it: "1st", "2nd", "3rd", "11th", "22nd". */
export function ordinal(n: number): string {
  const tens = n % 100
  if (tens >= 11 && tens <= 13) return `${n}th`
  return `${n}${({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[n % 10] ?? 'th'}`
}

/**
 * A time of day, "03:41 PM" or "15:41" as the viewer's locale writes it, in their own clock.
 *
 * Hours and minutes, never seconds: nothing on a screen is acted on to the second, and the same
 * instant written with seconds on one page and without on the next reads as two different times.
 * Two digits for the hour, like `dateTime` and `FactTime`, so a column of times lines up.
 */
export function clockTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

/**
 * How long ago, measured against the server's clock rather than the browser's.
 *
 * The browser's clock is not the authority for anything here and is routinely wrong on a machine
 * that has been asleep, so every screen showing an age passes the `now` the server sent.
 */
export function ago(iso: string | null, now: string): string {
  if (!iso) return 'never'

  const seconds = Math.round((Date.parse(now) - Date.parse(iso)) / 1000)

  if (seconds < 0) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.round(seconds / 3600)}h ago`

  return `${Math.round(seconds / 86_400)}d ago`
}

/** When a list was last read, and the server's clock at the time it said so. */
export type Reading = { at: string | null; now: string }

/**
 * The oldest of several readings, or null when any of them has never been taken.
 *
 * A card built from two lists is only as fresh as the older one, so it gives one age rather than
 * one per list: nothing on it is older than that. Each reading is measured against its own `now`,
 * because the two answers can come from the server a moment apart.
 */
export function oldestReading(readings: Reading[]): { at: string; now: string } | null {
  let oldest: { at: string; now: string } | null = null
  let oldestAge = -Infinity

  for (const { at, now } of readings) {
    if (!at) return null

    const age = Date.parse(now) - Date.parse(at)
    if (age > oldestAge) {
      oldest = { at, now }
      oldestAge = age
    }
  }

  return oldest
}

/**
 * The accounts that could not be tied to a person, in one line: "Not linked to Discord or Modbot".
 * Empty when nothing is missing.
 */
export function notLinkedTo(names: string[]): string {
  if (names.length === 0) return ''
  if (names.length === 1) return `Not linked to ${names[0]}`

  return `Not linked to ${names.slice(0, -1).join(', ')} or ${names[names.length - 1]}`
}

/**
 * How long something has been going on, against the server's clock, in the same steps as
 * {@link ago} and without the "ago". "Last seen 2d ago" and "known for 300d" are different
 * questions about the same person, and the People page asks both.
 */
export function howLong(iso: string | null, now: string): string {
  if (!iso) return 'never'

  const seconds = Math.round((Date.parse(now) - Date.parse(iso)) / 1000)

  if (seconds < 60) return `${Math.max(0, seconds)}s`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  if (seconds < 86_400) return `${Math.round(seconds / 3600)}h`

  return `${Math.round(seconds / 86_400)}d`
}

/**
 * A length of time given in minutes, in whole units: "16 min", "3 h 6 min", "2 d 4 h".
 *
 * Never a decimal. "3.1 h" makes the reader work out that .1 of an hour is six minutes, and most
 * do not. Two units at most, the larger first, and the smaller left out when it is zero ("3 h"):
 * past a day the minutes are noise. Rounded to the minute, or to the hour past a day, before the
 * unit is chosen, so 59.7 minutes reads "1 h" and not "60 min".
 */
export function lengthOfTime(totalMinutes: number): string {
  const wholeMinutes = Math.round(totalMinutes)

  if (wholeMinutes < 60) return `${wholeMinutes} min`

  if (wholeMinutes < 24 * 60) {
    const hours = Math.floor(wholeMinutes / 60)
    const rest = wholeMinutes % 60
    return rest ? `${hours} h ${rest} min` : `${hours} h`
  }

  const wholeHours = Math.round(totalMinutes / 60)
  const days = Math.floor(wholeHours / 24)
  const rest = wholeHours % 24
  return rest ? `${days} d ${rest} h` : `${days} d`
}

/** A duration in seconds: "45 seconds" under a minute, and {@link lengthOfTime} from there. */
export function duration(seconds: number): string {
  const wholeSeconds = Math.round(seconds)
  if (wholeSeconds < 60) return `${wholeSeconds} ${plural(wholeSeconds, 'second')}`
  return lengthOfTime(seconds / 60)
}

export const compact = (n: number): string =>
  Math.abs(n) >= 10_000
    ? `${(n / 1000).toFixed(n % 1000 === 0 ? 0 : 1)}K`
    : Number.isInteger(n)
      ? n.toLocaleString()
      : n.toFixed(1)

export type Point = { day: string; value: number }

/**
 * Fills in the days a sparse series does not carry a row for.
 *
 * Daily totals and group-info facts are both written only on days something happened, so the raw series
 * has holes. `carry` is right for a level — a headcount stays what it was — and `zero` is right for
 * a count: no bans recorded is zero bans, not an unknown.
 */
export function denseDays(
  from: string,
  to: string,
  points: Point[],
  mode: 'zero' | 'carry',
): Point[] {
  const byDay = new Map(points.map((p) => [p.day, p.value]))
  const out: Point[] = []

  const start = new Date(`${from}T00:00:00Z`)
  const end = new Date(`${to}T00:00:00Z`)

  let last = 0
  let started = mode === 'zero'

  for (let d = start; d <= end; d = new Date(d.getTime() + 86_400_000)) {
    const day = d.toISOString().slice(0, 10)
    const value = byDay.get(day)

    if (value !== undefined) {
      last = value
      started = true
    }

    // A carried series does not begin until it has been observed once. Starting it at zero would
    // draw a cliff from nothing up to the first real reading, which nothing in the data supports.
    if (started) out.push({ day, value: mode === 'carry' ? last : (value ?? 0) })
  }

  return out
}

/**
 * How open an instance is, in a word a member would use.
 *
 * A word this build has not seen is shown as VRChat wrote it. It is still the real answer, and
 * showing it beats replacing it with "unknown".
 */
export function access(groupAccessType: string | null): string | null {
  if (!groupAccessType) return null

  return (
    ({ members: 'Group members', plus: 'Members and friends', public: 'Anyone' } as Record<string, string>)[
      groupAccessType
    ] ?? groupAccessType
  )
}

/**
 * Who may join a group instance, in the game's own words: "Group", "Group+", "Group Public". The
 * words a moderator sees on the instance in VRChat, so a row here and the game agree. A word this
 * build has not seen is shown as VRChat wrote it.
 */
export function accessInGame(groupAccessType: string | null): string | null {
  if (!groupAccessType) return null

  return ({ members: 'Group', plus: 'Group+', public: 'Group Public' } as Record<string, string>)[groupAccessType] ?? groupAccessType
}

/** A time of day the way people say it: "8:04 PM", not "08:04 PM". */
export function timeOfDay(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
}

/**
 * When something ran, as one range: "Sep 26, 6:02–7:21 PM" inside a day, "Sep 19, 6:01 PM – Sep 20,
 * 12:11 AM" across midnight, "Sep 26, 8:04 PM –" while it is still going. Replaces a start and an
 * end stacked in one cell, which a narrow screen cut off mid-date.
 */
export function whenRange(from: string, to: string | null): string {
  const withYear = needsYear(...[from, to].filter((d): d is string => d !== null))
  const start = `${formatDay(from, withYear)}, ${timeOfDay(from)}`
  if (!to) return `${start} –`

  if (new Date(from).toDateString() === new Date(to).toDateString()) {
    const a = timeOfDay(from)
    const b = timeOfDay(to)
    // "6:02 PM–7:21 PM" reads as "6:02–7:21 PM" when both halves share the AM/PM.
    const shared = a.slice(-3) === b.slice(-3) && /\s?[AP]M$/i.test(a) ? a.replace(/\s?[AP]M$/i, '') : a
    return `${formatDay(from, withYear)}, ${shared}–${b}`
  }

  return `${start} – ${formatDay(to, withYear)}, ${timeOfDay(to)}`
}

/**
 * A head count as text: `52`, or `80?` when it is unsure -- taken from VRChat's `n_users` because the
 * instance's page had no `userCount`. `n_users` ran up to about thirty high on a busy evening, so the
 * mark stays on the number wherever it goes. `format` writes the number itself.
 */
export function headCountText(count: number, unsure: boolean, format: (n: number) => string = String): string {
  return unsure ? `${format(count)}?` : format(count)
}
