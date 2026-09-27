import { useSyncExternalStore } from 'react'
import { api, type SyncHealth } from '@/lib/api'
import { statusRows, type StatusRow } from '@/lib/status'
import { useGateHealth } from '@/lib/useGateHealth'

/**
 * One poll of what the status rows read, for the whole page, however many places draw them.
 *
 * The sidebar's rows, the phone's menu and the Now page's one line all say the same thing, and
 * three timers would ask the server three times as often for it. The same shape as
 * `useGateHealth`, whose poll this shares for the VRChat row.
 */

/** Slower than the Health page's own poll: this sits on every screen, and the sync read is not cheap. */
const EVERY_MS = 30_000

type Reading = { health: SyncHealth | null; databaseReachable: boolean | null }

let reading: Reading = { health: null, databaseReachable: null }
const listeners = new Set<() => void>()
let timer: ReturnType<typeof setInterval> | null = null

function publish(next: Partial<Reading>) {
  reading = { ...reading, ...next }
  for (const listener of listeners) listener()
}

function load() {
  api
    .syncHealth()
    .then((health) => publish({ health }))
    .catch(() => publish({ health: null }))

  api
    .databaseHealth()
    .then((ok) => publish({ databaseReachable: ok }))
    .catch(() => publish({ databaseReachable: null }))
}

function subscribe(listener: () => void) {
  listeners.add(listener)

  if (listeners.size === 1) {
    load()
    timer = setInterval(load, EVERY_MS)
  }

  return () => {
    listeners.delete(listener)
    if (listeners.size === 0 && timer) {
      clearInterval(timer)
      timer = null
    }
  }
}

/** The rows at the foot of the sidebar, as they stand. A part that has not answered reads "unknown". */
export function useStatusRows(): StatusRow[] {
  const { gate, failed } = useGateHealth()
  const { health, databaseReachable } = useSyncExternalStore(subscribe, () => reading)

  return statusRows({ gate: failed ? null : (gate?.status ?? null), health, databaseReachable })
}
