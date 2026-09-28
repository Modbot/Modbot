/**
 * Runs `run` at once, then at most once every `gapMs` while calls keep coming: a call inside the
 * gap is held, and every call held in one gap runs once when it ends.
 *
 * For redrawing on live events from a busy source. Waiting for a quiet moment instead -- the
 * settle Live and `useLiveVersion` use for VRChat -- would keep a list stale for as long as a busy
 * voice server kept changing.
 *
 * Pure: the clock and timers are handed in, so the rule can be tested in Node.
 */
export type Throttle = {
  (): void
  /** Drops a held call. */
  cancel(): void
}

export type ThrottleTimers = {
  now: () => number
  setTimer: (run: () => void, ms: number) => unknown
  clearTimer: (handle: unknown) => void
}

const BROWSER: ThrottleTimers = {
  now: () => performance.now(),
  setTimer: (run, ms) => setTimeout(run, ms),
  clearTimer: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

export function throttle(run: () => void, gapMs: number, timers: ThrottleTimers = BROWSER): Throttle {
  let last = -Infinity
  let held = false
  let handle: unknown

  const fire = () => {
    held = false
    last = timers.now()
    run()
  }

  const call = () => {
    if (held) return

    const wait = last + gapMs - timers.now()
    if (wait <= 0) {
      fire()
      return
    }

    held = true
    handle = timers.setTimer(fire, wait)
  }

  call.cancel = () => {
    if (held) timers.clearTimer(handle)
    held = false
  }

  return call
}
