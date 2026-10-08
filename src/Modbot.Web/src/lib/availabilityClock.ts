import { useSyncExternalStore } from 'react'
import { browserClock, type ClockFormat } from './availabilityZones.ts'

/**
 * Whether the Availability grids write hours as `17:00` or `5 PM`. One choice for both tabs, kept in
 * the browser. Until one is made it is the clock the browser's own language uses.
 */
const KEY = 'modbot.availability.clock'

export function recallClock(): ClockFormat | null {
  try {
    const raw = localStorage.getItem(KEY)
    return raw === '24h' || raw === '12h' ? raw : null
  } catch {
    return null
  }
}

export function rememberClock(clock: ClockFormat): void {
  try {
    localStorage.setItem(KEY, clock)
  } catch {
    // A blocked store forgets; the page still opens on the browser's clock.
  }
}

/** The clock to open on: the one last picked, or else the browser's. */
export function firstClock(): ClockFormat {
  return recallClock() ?? browserClock()
}

let current: ClockFormat | null = null
const listeners = new Set<() => void>()

function read(): ClockFormat {
  current ??= firstClock()
  return current
}

function subscribe(onChange: () => void) {
  listeners.add(onChange)
  return () => {
    listeners.delete(onChange)
  }
}

function choose(clock: ClockFormat) {
  current = clock
  rememberClock(clock)
  for (const listener of listeners) listener()
}

/** The clock in use and a way to change it; every grid on the page follows the change. */
export function useClock(): [ClockFormat, (clock: ClockFormat) => void] {
  return [useSyncExternalStore(subscribe, read, () => '24h' as const), choose]
}
