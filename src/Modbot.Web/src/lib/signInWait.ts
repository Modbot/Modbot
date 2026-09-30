/**
 * The words of the sign-in wait banner (foundation spec 4.1.2).
 *
 * No imports, so the Node test runner can load it as it is.
 */

/**
 * How long is left, in Modbot's units: "59m 32s", "1m 1s", "45s", "2m", and "now" at zero or
 * below. The same shape as `lengthOfTime` in format.ts, written out here rather than imported so
 * this file stays free of imports; unlike a length, a countdown keeps its seconds.
 */
export function timeLeftText(seconds: number): string {
  const whole = Math.max(0, Math.ceil(seconds))
  if (whole === 0) return 'now'

  const minutes = Math.floor(whole / 60)
  const rest = whole % 60

  if (minutes === 0) return `${rest}s`
  if (rest === 0) return `${minutes}m`
  return `${minutes}m ${rest}s`
}

/** The banner's text, exactly as the maintainer wrote it. */
export function signInWaitText(seconds: number): string {
  const left = timeLeftText(seconds)
  return `Service account authentication issue - ratelimited - retrying ${left === 'now' ? 'now' : `in ${left}`}`
}
