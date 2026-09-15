/**
 * The words of the sign-in wait banner (foundation spec 4.1.2).
 *
 * No imports, so the Node test runner can load it as it is.
 */

function plural(n: number, word: string): string {
  return `${n} ${word}${n === 1 ? '' : 's'}`
}

/**
 * How long is left, in words: "59 minutes and 32 seconds", "1 minute and 1 second",
 * "45 seconds", "2 minutes", and "now" at zero or below.
 */
export function timeLeftText(seconds: number): string {
  const whole = Math.max(0, Math.ceil(seconds))
  if (whole === 0) return 'now'

  const minutes = Math.floor(whole / 60)
  const rest = whole % 60

  if (minutes === 0) return plural(rest, 'second')
  if (rest === 0) return plural(minutes, 'minute')
  return `${plural(minutes, 'minute')} and ${plural(rest, 'second')}`
}

/** The banner's text, exactly as the maintainer wrote it. */
export function signInWaitText(seconds: number): string {
  const left = timeLeftText(seconds)
  return `Service account authentication issue - ratelimited - retrying ${left === 'now' ? 'now' : `in ${left}`}`
}
