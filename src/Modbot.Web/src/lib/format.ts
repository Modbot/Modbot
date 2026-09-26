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
