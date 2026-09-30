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

/** The viewer's own calendar day an instant falls on, as "2026-09-27", for telling two days apart. */
export function localDayKey(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/**
 * A day as a list heading: "Today", "Yesterday", otherwise "Sep 25", with the year when it is not
 * the year `now` is in.
 *
 * `now` is the server's clock, as with {@link ago}: a heading that says "Today" over last night's
 * rows because the browser's clock is wrong answers "when did this happen?" wrongly. The day
 * boundaries are the viewer's own, since "yesterday" means the day before theirs.
 */
export function dayHeading(iso: string, now: string): string {
  const day = localDayKey(iso)
  if (day === localDayKey(now)) return 'Today'

  const yesterday = new Date(now)
  yesterday.setDate(yesterday.getDate() - 1)
  if (day === localDayKey(yesterday.toISOString())) return 'Yesterday'

  return formatDay(iso, new Date(iso).getFullYear() !== new Date(now).getFullYear())
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
 * A time of day the way people say it, in the viewer's own clock: "8:04 PM", not "08:04 PM", or
 * "20:04" where the viewer's locale counts to 24.
 *
 * The one way every screen writes a time. There were two until 2026-09-28, one with the leading
 * zero and one without, and the same moment read "08:04 PM" in the audit log and "8:04 PM" on an
 * instance -- close enough to look like the same time, far enough apart to make a moderator check.
 * The zero bought a column of times that lined up; that was not worth two spellings of one time.
 *
 * Hours and minutes, never seconds: nothing on a screen is acted on to the second, and the same
 * instant written with seconds on one page and without on the next reads as two different times.
 */
export function timeOfDay(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
}

/**
 * An instant, "Sep 27, 8:44 PM": the day first, then the time, in the viewer's own clock, because
 * that is the clock they will act in. The year only when it is not this year ({@link needsYear});
 * pass `withYear` for two instants shown as one span, so both carry it or neither does.
 */
export function dateTime(iso: string, withYear: boolean = needsYear(iso)): string {
  return new Date(iso).toLocaleString(undefined, {
    year: withYear ? 'numeric' : undefined,
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}

/**
 * An instant with its weekday, "Fri, Sep 25, 1:00 PM", for a planned event, where the day of the
 * week is what people plan around. Otherwise the same as {@link dateTime}.
 */
export function dateTimeWithWeekday(iso: string, withYear: boolean = needsYear(iso)): string {
  return new Date(iso).toLocaleString(undefined, {
    weekday: 'short',
    year: withYear ? 'numeric' : undefined,
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}

const DAY_SECONDS = 86_400
const DAYS_IN_MONTH = 30.44
const DAYS_IN_YEAR = 365

/**
 * A number with its unit, and a second one when it is not zero: "4h", "4h 40m".
 *
 * Every length and every age in Modbot is written in the same six units -- y, mth, d, h, m, s --
 * and never in weeks. The unit sits against its number, one space parts the two units, and the
 * smaller is left out when it is zero: "4h 40m" is read as one length, where "4 h 40 min" broke
 * into four words and the eye paired the wrong ones.
 */
function units(big: number, bigUnit: string, small = 0, smallUnit = ''): string {
  return small ? `${big}${bigUnit} ${small}${smallUnit}` : `${big}${bigUnit}`
}

/**
 * An age in the largest unit that still reads at a glance: "40s", "12m", "5h", "30d", "3mth",
 * "1y". Days up to 45, because "38d" is still easy to picture and more exact than "1mth". Past
 * that, whole months and then whole years that have passed, the way people say an age: something
 * a year and a half old is "1y" until it is two. "563d" made the reader do the sum.
 */
function age(seconds: number): string {
  if (seconds < 60) return units(Math.max(0, seconds), 's')
  if (seconds < 3600) return units(Math.round(seconds / 60), 'm')
  if (seconds < DAY_SECONDS) return units(Math.round(seconds / 3600), 'h')

  const days = Math.round(seconds / DAY_SECONDS)
  if (days < 45) return units(days, 'd')
  if (days < DAYS_IN_YEAR) return units(Math.floor(days / DAYS_IN_MONTH), 'mth')
  return units(Math.floor(days / DAYS_IN_YEAR), 'y')
}

/**
 * How long ago, measured against the server's clock rather than the browser's, in the steps of
 * {@link age}: "5h ago", "30d ago", "3mth ago", "1y ago".
 *
 * The browser's clock is not the authority for anything here and is routinely wrong on a machine
 * that has been asleep, so every screen showing an age passes the `now` the server sent.
 */
export function ago(iso: string | null, now: string): string {
  if (!iso) return 'never'

  const seconds = Math.round((Date.parse(now) - Date.parse(iso)) / 1000)

  if (seconds < 0) return 'just now'
  return `${age(seconds)} ago`
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
 * {@link ago} and without the "ago". "Last seen 2d ago" and "known for 1y" are different
 * questions about the same person, and the People page asks both.
 */
export function howLong(iso: string | null, now: string): string {
  if (!iso) return 'never'

  return age(Math.round((Date.parse(now) - Date.parse(iso)) / 1000))
}

/**
 * A length of time given in minutes, in whole {@link units}: "16m", "3h 6m", "2d 4h", "1mth 3d",
 * "1y 2mth".
 *
 * Never a decimal. "3.1h" makes the reader work out that .1 of an hour is six minutes, and most
 * do not. Two units at most, the larger first, and the smaller left out when it is zero ("3h"):
 * past a day the minutes are noise, past a month the hours are. Rounded to the minute, to the
 * hour past a day, and to the day past a month, before the unit is chosen, so 59.7 minutes reads
 * "1h" and not "60m". A month is 30.44 days and a year 365, as in an age; there are no weeks.
 * Every length on every page comes through here, so they all read alike.
 */
export function lengthOfTime(totalMinutes: number): string {
  const minutes = Math.round(totalMinutes)
  if (minutes < 60) return units(minutes, 'm')
  if (minutes < 24 * 60) return units(Math.floor(minutes / 60), 'h', minutes % 60, 'm')

  const hours = Math.round(totalMinutes / 60)
  const days = Math.round(totalMinutes / (24 * 60))
  if (days < 31) return units(Math.floor(hours / 24), 'd', hours % 24, 'h')

  // The nearest month, not the month begun, so a 90-day setting reads "3mth" and not "2mth 29d".
  if (days < DAYS_IN_YEAR) {
    const months = Math.round(days / DAYS_IN_MONTH)
    if (months >= 12) return units(1, 'y')
    return units(months, 'mth', Math.max(0, Math.round(days - months * DAYS_IN_MONTH)), 'd')
  }

  const years = Math.floor(days / DAYS_IN_YEAR)
  const months = Math.round((days - years * DAYS_IN_YEAR) / DAYS_IN_MONTH)
  return months >= 12 ? units(years + 1, 'y') : units(years, 'y', months, 'mth')
}

/** A duration in seconds: "45s" under a minute, and {@link lengthOfTime} from there. */
export function duration(seconds: number): string {
  const wholeSeconds = Math.round(seconds)
  if (wholeSeconds < 60) return units(wholeSeconds, 's')
  return lengthOfTime(seconds / 60)
}

/**
 * How long a call took, from milliseconds: "412ms", then "1.2s". A measurement of a machine, not
 * a length a person lived through, so it keeps a decimal where {@link duration} never would.
 */
export function elapsed(ms: number): string {
  return ms < 1000 ? `${Math.round(ms)}ms` : `${(ms / 1000).toFixed(1)}s`
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
