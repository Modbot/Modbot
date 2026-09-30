const DAY_SECONDS = 86_400
const DAYS_IN_MONTH = 30.44
const DAYS_IN_YEAR = 365

// The same steps as `units` and `age` in the moderator app's src/Modbot.Web/src/lib/format.ts and
// TimeWords.Age on the server. This app is its own package and cannot import them, so the copy is
// kept in step by hand, as is the one in src/Modbot.My.Web: change one, change them all.

/** A number with its unit against it, and a second one when it is not zero: "4h", "4h 40m". */
function units(big: number, bigUnit: string, small = 0, smallUnit = ''): string {
  return small ? `${big}${bigUnit} ${small}${smallUnit}` : `${big}${bigUnit}`
}

/**
 * An age in the largest unit that still reads at a glance: "40s", "12m", "5h", "30d", "3mth",
 * "1y". Days up to 45, then whole months and whole years that have passed. No weeks.
 */
function age(totalSeconds: number): string {
  const seconds = Math.max(0, Math.round(totalSeconds))
  if (seconds < 60) return units(seconds, 's')
  if (seconds < 3600) return units(Math.round(seconds / 60), 'm')
  if (seconds < DAY_SECONDS) return units(Math.round(seconds / 3600), 'h')

  const days = Math.round(seconds / DAY_SECONDS)
  if (days < 45) return units(days, 'd')
  if (days < DAYS_IN_YEAR) return units(Math.floor(days / DAYS_IN_MONTH), 'mth')
  return units(Math.floor(days / DAYS_IN_YEAR), 'y')
}

/**
 * How long ago, in the steps of {@link age}: "45s ago", "5m ago", "3h ago", "2y ago". "just now"
 * for a moment the browser's clock puts in the future, and nothing for a missing date.
 */
export function ago(iso: string, now: number = Date.now()): string {
  const then = Date.parse(iso)
  if (Number.isNaN(then) || then <= 0) return ''

  const seconds = Math.round((now - then) / 1000)
  if (seconds < 0) return 'just now'
  return `${age(seconds)} ago`
}

export function when(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '—'
}

export function host(url: string): string {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

/**
 * A clock correction in seconds, signed: "+3.0s". A measurement of a machine, so it keeps its
 * decimal, as `elapsed` does in the moderator app, but the unit sits against the number like every
 * other length.
 */
export function clockText(offsetMs: number | null): string {
  if (offsetMs === null) return '—'
  const seconds = offsetMs / 1000
  return `${seconds >= 0 ? '+' : ''}${seconds.toFixed(1)}s`
}
