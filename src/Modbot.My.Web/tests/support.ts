import type { Timers } from '../src/lib/retry.ts'

/** Lets every promise that is ready to run, run. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await new Promise<void>((resolve) => setImmediate(resolve))
}

/** Timers a test moves by hand. */
export function fakeTimers(): {
  timers: Timers
  /** Moves time on, running what comes due in order, and lets the promises that follow settle. */
  advance: (ms: number) => Promise<void>
  /** How long until each pending timer fires, soonest first. */
  pending: () => number[]
} {
  let now = 0
  let seq = 0
  const due: { at: number; id: number; run: () => void }[] = []

  const timers: Timers = {
    set: (run, ms) => {
      const id = ++seq
      due.push({ at: now + ms, id, run })
      return id
    },
    clear: (handle) => {
      const index = due.findIndex((d) => d.id === handle)
      if (index >= 0) due.splice(index, 1)
    },
  }

  const next = (until: number) =>
    due.filter((d) => d.at <= until).sort((a, b) => a.at - b.at || a.id - b.id)[0]

  return {
    timers,
    advance: async (ms) => {
      const until = now + ms
      await settle()
      for (let timer = next(until); timer; timer = next(until)) {
        due.splice(due.indexOf(timer), 1)
        now = timer.at
        timer.run()
        await settle()
      }
      now = until
    },
    pending: () => due.map((d) => d.at - now).sort((a, b) => a - b),
  }
}

/** A localStorage for Node, where there is none. */
export function fakeLocalStorage(): Map<string, string> {
  const store = new Map<string, string>()
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: (key: string) => store.get(key) ?? null,
      setItem: (key: string, value: string) => void store.set(key, String(value)),
      removeItem: (key: string) => void store.delete(key),
      clear: () => store.clear(),
    },
  })
  return store
}
