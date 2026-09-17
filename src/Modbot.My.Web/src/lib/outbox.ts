import { api, isWorthRetrying } from './api.ts'
import { browserTimers, keepTrying, type Timers } from './retry.ts'
import { loadOutbox, saveOutbox, type OutboxEntry } from './storage.ts'

export type Outbox = {
  /** Queues an address and tries to send it right away. Queuing one already there sends again. */
  add: (url: string) => void
  /** What is still to be sent. The same array until something changes, so React can compare it. */
  entries: () => readonly OutboxEntry[]
  subscribe: (listener: () => void) => () => void
  /** Sends what an earlier page load left behind, and again whenever the browser comes back online. */
  start: () => void
}

/**
 * The addresses this browser still owes the server.
 *
 * `POST /api/local-register` is how a page view reaches Modbot Cloud (central services spec 2.3.1).
 * When it cannot be delivered — my.modbot.co unreachable, or up but unable to reach Cloud — the
 * address waits in localStorage and is sent in the background: again after 5 s, 10 s, 20 s and so
 * on up to five minutes while the tab is open, at once when the browser comes back online, and on
 * the next page load. It leaves the queue only when the server said it took it, or answered that
 * it never will (a 4xx other than "try later"), so the page never claims a send it did not get.
 */
export function createOutbox(send: (url: string) => Promise<void>, timers: Timers = browserTimers): Outbox {
  const listeners = new Set<() => void>()
  let entries: readonly OutboxEntry[] = loadOutbox()
  let started = false

  const replace = (next: readonly OutboxEntry[]) => {
    entries = next
    saveOutbox(next)
    for (const listener of listeners) listener()
  }

  const without = (url: string) => entries.filter((e) => e.url !== url)

  const retry = keepTrying(async () => {
    for (const entry of entries) {
      try {
        await send(entry.url)
      } catch (error) {
        if (!isWorthRetrying(error)) {
          replace(without(entry.url))
          continue
        }
        replace(entries.map((e) => (e.url === entry.url ? { ...e, tries: e.tries + 1 } : e)))
        return false
      }
      replace(without(entry.url))
    }
    return true
  }, timers)

  const onOnline = () => retry.now()

  return {
    add: (url) => {
      if (!entries.some((e) => e.url === url))
        replace([...entries, { url, addedAt: new Date().toISOString(), tries: 0 }])
      retry.now()
    },
    entries: () => entries,
    subscribe: (listener) => {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    start: () => {
      if (started) return
      started = true
      if (typeof window !== 'undefined') window.addEventListener('online', onOnline)
      if (entries.length > 0) retry.now()
    },
  }
}

export const outbox = createOutbox(api.localRegister)
