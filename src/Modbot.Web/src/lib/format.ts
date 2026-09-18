/**
 * Formatting and series preparation.
 *
 * Plain functions, deliberately not in a component file: the fast-refresh boundary only works when
 * a module exports components or values, not both, and these are shared by every screen.
 */

const SOURCE_LABEL: Record<string, string> = {
  AuditLog: 'VRChat',
  SyncDiff: 'Sync',
  Client: 'Client',
  Discord: 'Discord',
  Manual: 'Manual',
  Modbot: 'Modbot',
  Import: 'Import',
}

/**
 * The name of the system a fact came from.
 *
 * `AuditLog` reads as "VRChat" because that is whose record it is — spec 5.9.5's chips are named
 * for the sources people think in, not for the enum members.
 */
export const sourceLabel = (source: string): string => SOURCE_LABEL[source] ?? source

export function formatDay(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
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

/** A duration in seconds, said the way a person would say it. */
export function duration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)} seconds`
  if (seconds < 3600) return `${Math.round(seconds / 60)} minutes`
  return `${(seconds / 3600).toFixed(1)} hours`
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
