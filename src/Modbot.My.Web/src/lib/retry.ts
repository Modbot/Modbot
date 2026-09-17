/** The wait after the first failure. Doubles each time. */
export const FIRST_RETRY_MS = 5_000

/** The longest wait between two tries. */
export const LONGEST_RETRY_MS = 5 * 60_000

/** How long to wait before try number `failures + 1`: 5 s, 10 s, 20 s, … up to five minutes. */
export function retryDelay(failures: number): number {
  const doublings = Math.max(0, failures - 1)
  return Math.min(LONGEST_RETRY_MS, FIRST_RETRY_MS * 2 ** doublings)
}

/** The timers to schedule with. The app uses the browser's; a test hands in its own. */
export type Timers = {
  set: (run: () => void, ms: number) => unknown
  clear: (handle: unknown) => void
}

export const browserTimers: Timers = {
  set: (run, ms) => setTimeout(run, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

export type Retrying = {
  /** Tries now, forgetting the backoff. A try already running is followed by one more. */
  now: () => void
  /** No further tries after this. One already running finishes but schedules nothing. */
  stop: () => void
}

/**
 * Keeps trying something until it succeeds.
 *
 * `attempt` resolves true when it is done and false when it should be tried again, after a wait
 * that doubles each time (`retryDelay`). A throw counts as false. `now()` is for a fresh reason to
 * try — the browser coming back online, something new to send — and starts the backoff over.
 */
export function keepTrying(attempt: () => Promise<boolean>, timers: Timers = browserTimers): Retrying {
  let failures = 0
  let timer: unknown = null
  let running = false
  let again = false
  let stopped = false

  const clearTimer = () => {
    if (timer !== null) timers.clear(timer)
    timer = null
  }

  const run = async () => {
    if (stopped) return
    if (running) {
      again = true
      return
    }

    clearTimer()
    running = true
    let done: boolean
    try {
      done = await attempt()
    } catch {
      done = false
    }
    running = false

    if (stopped) return

    if (again) {
      again = false
      failures = 0
      void run()
      return
    }

    if (done) {
      failures = 0
      return
    }

    failures += 1
    timer = timers.set(() => {
      timer = null
      void run()
    }, retryDelay(failures))
  }

  return {
    now: () => {
      failures = 0
      void run()
    },
    stop: () => {
      stopped = true
      clearTimer()
    },
  }
}
